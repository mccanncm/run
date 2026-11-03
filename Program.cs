using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using SfMarathonDashboard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression();
builder.Services.AddHealthChecks();

// App keys: bound from appsettings.json placeholders, overridden by Azure App Settings / user-secrets.
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.AddHttpClient();
builder.Services.AddDataProtection()
    .SetApplicationName("sf-marathon-dashboard")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(StoragePaths.DataDir(builder.Environment), "keys")));
builder.Services.AddSingleton<StravaService>();
builder.Services.Configure<StravaOptions>(builder.Configuration.GetSection("Strava"));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection("Google"));
builder.Services.Configure<GarminOptions>(builder.Configuration.GetSection("Garmin"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Log (without values) which key groups are still placeholders.
{
    var sp = app.Services;
    var strava = sp.GetRequiredService<IOptions<StravaOptions>>().Value;
    var google = sp.GetRequiredService<IOptions<GoogleOptions>>().Value;
    var garmin = sp.GetRequiredService<IOptions<GarminOptions>>().Value;
    if (!strava.ClientId.IsSet() || !strava.ClientSecret.IsSet()) app.Logger.LogInformation("Strava keys are not configured.");
    if (!google.ClientId.IsSet() || !google.ClientSecret.IsSet()) app.Logger.LogInformation("Google keys are not configured.");
    if (!garmin.ConsumerKey.IsSet() || !garmin.ConsumerSecret.IsSet()) app.Logger.LogInformation("Garmin keys are not configured.");
}

app.UseResponseCompression();

// Security headers. The dashboard is a self-contained page with inline script/style.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; base-uri 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api/strava") || ctx.Request.Path.StartsWithSegments("/auth"))
        h.CacheControl = "no-store";
    await next();
});

var types = new FileExtensionContentTypeProvider();
types.Mappings[".fit"] = "application/octet-stream";

app.UseDefaultFiles();   // serves wwwroot/index.html at /
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = types,
    OnPrepareResponse = c =>
    {
        // Always revalidate HTML so edits show up; cache everything else for an hour.
        c.Context.Response.Headers.CacheControl =
            c.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? "no-cache"
                : "public,max-age=3600";
    }
});

app.MapHealthChecks("/health");

// Shows which keys are set (true/false only, never the values).
app.MapGet("/api/config/status", (IOptions<StravaOptions> strava, IOptions<GoogleOptions> google, IOptions<GarminOptions> garmin) =>
    Results.Ok(new
    {
        app = new { adminKey = app.Services.GetRequiredService<IOptions<AppOptions>>().Value.AdminKey.IsSet() },
        strava = new { clientId = strava.Value.ClientId.IsSet(), clientSecret = strava.Value.ClientSecret.IsSet(), redirectUri = strava.Value.RedirectUri.IsSet() },
        google = new { clientId = google.Value.ClientId.IsSet(), clientSecret = google.Value.ClientSecret.IsSet() },
        garmin = new { consumerKey = garmin.Value.ConsumerKey.IsSet(), consumerSecret = garmin.Value.ConsumerSecret.IsSet() }
    }));

// Lists the files available under /downloads.
// ---- Strava live data (all protected by the admin key except /status) ----
app.MapGet("/api/strava/status", (StravaService s) =>
    Results.Ok(new { configured = s.Configured, connected = s.Connected, lastSync = s.LastSync }));

app.MapGet("/api/strava/activities", (HttpContext c, IOptions<AppOptions> o, StravaService s) =>
{
    if (!AdminAuth.IsAuthorized(c, o)) return Results.Unauthorized();
    var cached = s.LoadCache();
    return cached is null
        ? Results.NotFound(new { error = "No data yet. Press Refresh from Strava." })
        : Results.Ok(cached);
});

app.MapPost("/api/strava/refresh", async (HttpContext c, IOptions<AppOptions> o, StravaService s, CancellationToken ct) =>
{
    if (!AdminAuth.IsAuthorized(c, o)) return Results.Unauthorized();
    if (!s.Configured) return Results.Problem("Strava keys are not configured.", statusCode: 503);
    if (!s.Connected) return Results.Problem("Strava is not connected yet. Use Connect Strava.", statusCode: 409);
    try
    {
        return Results.Ok(await s.RefreshAsync(ct));
    }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Strava refresh failed");
        return Results.Problem(e.Message, statusCode: 502);
    }
});

app.MapPost("/api/strava/disconnect", (HttpContext c, IOptions<AppOptions> o, StravaService s) =>
{
    if (!AdminAuth.IsAuthorized(c, o)) return Results.Unauthorized();
    s.Disconnect();
    return Results.Ok(new { connected = false });
});

app.MapGet("/auth/strava/login", (HttpContext c, IOptions<AppOptions> o, StravaService s) =>
{
    if (!AdminAuth.IsAuthorized(c, o)) return Results.Unauthorized();
    if (!s.Configured) return Results.Problem("Strava keys are not configured.", statusCode: 503);
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    c.Response.Cookies.Append("sfm_oauth_state", state, new CookieOptions
    {
        HttpOnly = true,
        Secure = !StravaService.IsLocal(c),
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10)
    });
    return Results.Redirect(s.AuthorizeUrl(s.RedirectUri(c), state));
});

app.MapGet("/auth/strava/callback", async (HttpContext c, StravaService s, string? code, string? state, string? error, string? scope) =>
{
    var expected = c.Request.Cookies["sfm_oauth_state"];
    c.Response.Cookies.Delete("sfm_oauth_state");
    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return Results.Redirect("/?strava=denied");
    if (string.IsNullOrEmpty(expected) || expected != state) return Results.Redirect("/?strava=badstate");
    if (scope is null || !scope.Contains("activity:read")) return Results.Redirect("/?strava=scope");
    try
    {
        await s.ExchangeCodeAsync(code);
    }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Strava code exchange failed");
        return Results.Redirect("/?strava=error");
    }
    return Results.Redirect("/?strava=connected");
});

app.MapGet("/api/downloads", (IWebHostEnvironment env) =>
{
    var dir = Path.Combine(env.WebRootPath, "downloads");
    if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
    var files = new DirectoryInfo(dir).GetFiles()
        .OrderBy(f => f.Name)
        .Select(f => new { name = f.Name, url = $"/downloads/{f.Name}", bytes = f.Length });
    return Results.Ok(files);
});

app.Run();
