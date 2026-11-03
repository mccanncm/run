# SF Marathon Training Dashboard (ASP.NET Core)

A small ASP.NET Core (.NET 10) app that hosts the dark-themed training dashboard and its downloads.

- `wwwroot/index.html`: the dashboard (plan, hill workouts, recovery, analytics, calendar and Sheets exports)
- `wwwroot/downloads/`: Garmin workout steps (HTML and CSV) and the `.fit` workout files
- `GET /health`: health check
- `GET /api/downloads`: JSON list of downloadable files

## Run locally
```bash
dotnet run
# open the URL it prints (for example http://localhost:5000)
```

## Deploy to Azure App Service (Linux)
Requires the Azure CLI and the .NET 10 SDK. Run from this folder:

```bash
az login
az webapp up --name <unique-app-name> --resource-group sf-marathon-rg \
  --location westus2 --os-type linux --runtime "DOTNETCORE:10.0" --sku B1
az webapp update --name <unique-app-name> --resource-group sf-marathon-rg --https-only true
```
If the runtime string is rejected, list the valid values with `az webapp list-runtimes --os linux`.
`--sku F1` is the free tier, if you prefer it. App names must be unique across Azure (letters, numbers, hyphens).

## App keys and settings
`appsettings.json` holds placeholders (`<set-in-app-settings>`). Never put real keys in it. Settings are read as typed options in `Settings.cs`.

| Setting (Azure name) | Purpose |
|---|---|
| `Strava__ClientId`, `Strava__ClientSecret`, `Strava__RedirectUri` | Strava API app, for a future live activity sync |
| `Google__ClientId`, `Google__ClientSecret` | Google API (Sheets / Calendar) for direct export |
| `Garmin__ConsumerKey`, `Garmin__ConsumerSecret` | Garmin Connect developer program, if you get access |

In Azure, nested keys use a double underscore (`Strava__ClientId`) and override `appsettings.json`.

Set them in the portal (your App Service > Settings > Environment variables > App settings), or with the CLI:
```bash
az webapp config appsettings set --name <app> --resource-group sf-marathon-rg --settings \
  Strava__ClientId=xxxx Strava__ClientSecret=xxxx Strava__RedirectUri=https://<app>.azurewebsites.net/auth/strava/callback
```
For secrets, you can store them in Azure Key Vault and set the value to a reference such as `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/StravaSecret/)` (the app's managed identity needs read access).

Locally, use user-secrets instead of editing files:
```bash
dotnet user-secrets set "Strava:ClientSecret" "xxxx"
```
Check what is configured at `GET /api/config/status` (returns true/false only, never the values).

## Continuous deploy with GitHub Actions
1. In the Azure portal, open your App Service and choose Download publish profile.
2. In your GitHub repo, add a secret named `AZURE_WEBAPP_PUBLISH_PROFILE` with the file's contents.
3. Set `AZURE_WEBAPP_NAME` in `.github/workflows/azure-deploy.yml`, then push to `main`.

## Container option
`docker build -t sf-marathon . && docker run -p 8080:8080 sf-marathon`, then push to Azure Container Registry and point an App Service (Containers) at it.

## Updating the dashboard
Edit `wwwroot/index.html` and redeploy. The Strava data in the dashboard is a snapshot from Nov 2026 (May 1 to Oct 31), and the app has no live Strava connection.
