using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using SfMarathonDashboard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression();
builder.Services.AddHealthChecks();

// App keys: bound from appsettings.json placeholders, overridden by Azure App Settings / user-secrets.
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
        strava = new { clientId = strava.Value.ClientId.IsSet(), clientSecret = strava.Value.ClientSecret.IsSet(), redirectUri = strava.Value.RedirectUri.IsSet() },
        google = new { clientId = google.Value.ClientId.IsSet(), clientSecret = google.Value.ClientSecret.IsSet() },
        garmin = new { consumerKey = garmin.Value.ConsumerKey.IsSet(), consumerSecret = garmin.Value.ConsumerSecret.IsSet() }
    }));

// Lists the files available under /downloads.
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
