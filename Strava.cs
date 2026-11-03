using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace SfMarathonDashboard;

public record StravaTokens(string AccessToken, string RefreshToken, long ExpiresAt);

// Activities are shaped for the dashboard: [date "yyyy-MM-dd", type R/W/O, miles, feet, minutes]
public record SyncResult(DateTime SyncedAt, List<object[]> Activities);

public static class StoragePaths
{
    // On Azure App Service the HOME folder persists across restarts and deployments.
    public static string DataDir(IWebHostEnvironment env) =>
        Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null
            ? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? env.ContentRootPath, "data")
            : Path.Combine(env.ContentRootPath, "App_Data");
}

public static class AdminAuth
{
    public static bool IsAuthorized(HttpContext ctx, IOptions<AppOptions> options)
    {
        var expected = options.Value.AdminKey;
        if (!expected.IsSet()) return false; // no key configured: deny everything
        var supplied = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault()
                       ?? ctx.Request.Query["key"].FirstOrDefault()
                       ?? "";
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public class StravaService
{
    private readonly IHttpClientFactory _http;
    private readonly StravaOptions _opt;
    private readonly IDataProtector _protector;
    private readonly ILogger<StravaService> _log;
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StravaService(IHttpClientFactory http, IOptions<StravaOptions> opt, IDataProtectionProvider dp,
                         IWebHostEnvironment env, ILogger<StravaService> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
        _protector = dp.CreateProtector("SfMarathon.Strava.Tokens");
        _dir = StoragePaths.DataDir(env);
        Directory.CreateDirectory(_dir);
    }

    private string TokenPath => Path.Combine(_dir, "strava_tokens.bin");
    private string CachePath => Path.Combine(_dir, "activities.json");

    public bool Configured => _opt.ClientId.IsSet() && _opt.ClientSecret.IsSet();
    public bool Connected => LoadTokens() is not null;
    public DateTime? LastSync => LoadCache()?.SyncedAt;

    public static bool IsLocal(HttpContext c) => c.Request.Host.Host is "localhost" or "127.0.0.1";

    public string RedirectUri(HttpContext c) =>
        _opt.RedirectUri.IsSet()
            ? _opt.RedirectUri
            : $"{(IsLocal(c) ? c.Request.Scheme : "https")}://{c.Request.Host}/auth/strava/callback";

    public string AuthorizeUrl(string redirectUri, string state) =>
        "https://www.strava.com/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(_opt.ClientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_type=code&approval_prompt=auto&scope=read,activity:read_all" +
        $"&state={Uri.EscapeDataString(state)}";

    // ---- tokens (encrypted at rest with ASP.NET Data Protection) ----
    private StravaTokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var json = _protector.Unprotect(File.ReadAllBytes(TokenPath));
            return JsonSerializer.Deserialize<StravaTokens>(json);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not read stored Strava tokens; reconnect Strava.");
            return null;
        }
    }

    private void SaveTokens(StravaTokens t) =>
        File.WriteAllBytes(TokenPath, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(t)));

    public void Disconnect()
    {
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
    }

    private async Task<StravaTokens> TokenRequestAsync(Dictionary<string, string> form)
    {
        form["client_id"] = _opt.ClientId;
        form["client_secret"] = _opt.ClientSecret;
        var client = _http.CreateClient();
        using var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Strava token request failed ({(int)res.StatusCode}).");
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var tokens = new StravaTokens(
            r.GetProperty("access_token").GetString()!,
            r.GetProperty("refresh_token").GetString()!,
            r.GetProperty("expires_at").GetInt64());
        SaveTokens(tokens);
        return tokens;
    }

    public Task ExchangeCodeAsync(string code) =>
        TokenRequestAsync(new Dictionary<string, string> { ["code"] = code, ["grant_type"] = "authorization_code" });

    private async Task<string> AccessTokenAsync()
    {
        var t = LoadTokens() ?? throw new InvalidOperationException("Strava is not connected.");
        if (t.ExpiresAt - 120 > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return t.AccessToken;
        t = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = t.RefreshToken
        });
        return t.AccessToken;
    }

    // ---- activities ----
    public SyncResult? LoadCache()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<SyncResult>(File.ReadAllText(CachePath))
                : null;
        }
        catch { return null; }
    }

    public async Task<SyncResult> RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Strava rate limits are 100 requests / 15 min, so don't re-fetch more than once a minute.
            var cached = LoadCache();
            if (cached is not null && DateTime.UtcNow - cached.SyncedAt < TimeSpan.FromSeconds(60)) return cached;

            var token = await AccessTokenAsync();
            var after = DateTimeOffset.UtcNow.AddDays(-190).ToUnixTimeSeconds(); // a bit over 26 weeks
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var list = new List<object[]>();
            for (var page = 1; page <= 10; page++)
            {
                using var res = await client.GetAsync(
                    $"https://www.strava.com/api/v3/athlete/activities?after={after}&per_page=200&page={page}", ct);
                if (!res.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Strava API error ({(int)res.StatusCode}).");
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var count = 0;
                foreach (var a in doc.RootElement.EnumerateArray())
                {
                    count++;
                    list.Add(Map(a));
                }
                if (count < 200) break;
            }

            list.Sort((x, y) => string.CompareOrdinal((string)x[0], (string)y[0]));
            var result = new SyncResult(DateTime.UtcNow, list);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(result));
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static object[] Map(JsonElement a)
    {
        var type = a.TryGetProperty("sport_type", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? ""
            : a.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
        var code = type is "Run" or "TrailRun" or "VirtualRun" ? "R"
                 : type is "Walk" or "Hike" ? "W"
                 : "O";
        var date = (a.GetProperty("start_date_local").GetString() ?? "")[..10];
        var meters = Num(a, "distance");
        var elevM = Num(a, "total_elevation_gain");
        var seconds = Num(a, "moving_time");
        return new object[]
        {
            date, code,
            Math.Round(meters / 1609.344, 2),
            (int)Math.Round(elevM * 3.28084),
            Math.Round(seconds / 60.0, 1)
        };
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
