namespace SfMarathonDashboard;

// Strongly typed app settings. Real values come from Azure App Settings (or user-secrets locally),
// never from this repo. In Azure, nested keys use a double underscore: Strava__ClientId.
public class StravaOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public class AppOptions
{
    // Protects connect/refresh/read of Strava data. Required for the live Strava features.
    public string AdminKey { get; set; } = "";
}

public class GoogleOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class GarminOptions
{
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
}

public static class SettingsExtensions
{
    // A value counts as "set" only if it is non-empty and not the "<set-in-app-settings>" placeholder.
    public static bool IsSet(this string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith('<');
}
