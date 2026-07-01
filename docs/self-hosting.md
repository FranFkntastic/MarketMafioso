# Self-Hosting the MarketMafioso Receiver

MarketMafioso's receiver is a private ASP.NET Core service with a bundled dashboard and a local SQLite database. It is meant for self-hosted or small trusted deployments, not public multi-user hosting.

The receiver enables persistent inventory snapshots, the inventory browser, diagnostics, and Market Acquisition dashboard requests. The in-game plugin can still run without it, but server-backed features will be unavailable.

## Requirements

- Docker with Docker Compose, or .NET 10 for direct hosting.
- A random client API key shared between the plugin and receiver.
- A dashboard username/password for browser login.
- Persistent storage for the SQLite database.
- Optional but recommended: HTTPS behind Caddy, nginx, or another reverse proxy.

Market Acquisition item search also needs a compatible XIV data gateway. Configure `MarketMafioso__XivDataBaseUrl` if you want the dashboard's item selector to resolve names to item IDs. Without it, inventory upload and browsing still work, but acquisition request creation is limited.

## Quick Docker Setup

The easiest path is the release bundle under `release/self-host/`. It uses the prebuilt server image instead of building the ASP.NET app locally.

```powershell
cd release/self-host
.\Setup-MarketMafiosoServer.ps1
```

The setup script generates `marketmafioso.env`, pulls the server image, starts the container, and prints the plugin endpoint/API key.

To update later:

```powershell
cd release/self-host
.\Update-MarketMafiosoServer.ps1
```

The update script backs up the SQLite database, pulls the latest image, and restarts the container.

## Manual Docker Setup

1. Copy the environment sample and replace every placeholder secret:

```powershell
Copy-Item docs/samples/marketmafioso.env.example marketmafioso.env
```

2. Edit `marketmafioso.env`:

```text
MarketMafioso__ClientApiKey=<random-client-key>
MarketMafioso__DashboardBootstrapUsername=marketmafioso
MarketMafioso__DashboardBootstrapPassword=<random-dashboard-password>
MarketMafioso__XivDataBaseUrl=<compatible-xivdata-url-if-using-acquisition>
```

3. Start the receiver from the repo-local compose file:

```powershell
docker compose up -d
```

4. Open the dashboard:

```text
http://localhost:5088/
```

5. Configure the plugin:

```text
Server URL:     http://localhost:5088/inventory
Client API Key: the same MarketMafioso__ClientApiKey value
```

The Docker path stores SQLite data under `./data/marketmafioso/` on the host.

## Direct .NET Hosting

Publish the server:

```powershell
.\src\MarketMafioso\tools\Publish-ServerRelease.ps1
```

For a self-contained Linux x64 build:

```powershell
.\src\MarketMafioso\tools\Publish-ServerRelease.ps1 -Runtime linux-x64
```

Copy the publish output to the host, then configure it with either environment variables or an appsettings file based on:

```text
src/MarketMafioso.Server/appsettings.SelfHost.example.json
```

Useful samples:

- `docs/samples/marketmafioso.service`
- `docs/samples/caddy.marketmafioso.example`
- `docs/samples/nginx.marketmafioso.example`

## Important Settings

```text
MarketMafioso__RequireApiKey=true
MarketMafioso__ClientApiKey=<shared-plugin-server-key>
MarketMafioso__BasePath=<path-prefix-or-empty>
MarketMafioso__PublicOrigin=<public-origin-for-dashboard-links>
MarketMafioso__DatabasePath=<sqlite-file-path>
MarketMafioso__RawJsonRetentionCount=20
MarketMafioso__SnapshotRetentionCount=500
MarketMafioso__DiagnosticsRetentionCount=5000
MarketMafioso__XivDataBaseUrl=<compatible-xivdata-url>
MarketMafioso__RequireDashboardAuth=true
MarketMafioso__DashboardBootstrapUsername=<dashboard-user>
MarketMafioso__DashboardBootstrapPassword=<dashboard-password>
```

The bootstrap dashboard user is created only when the receiver database has no dashboard users. Changing the bootstrap password later does not rotate an existing user's password.

## Reverse Proxy Notes

If hosting at the root of a domain, leave `MarketMafioso__BasePath` empty:

```text
https://marketmafioso.example.com/
```

If hosting under a path, set the base path and preserve that path when proxying:

```text
MarketMafioso__BasePath=/marketmafioso
MarketMafioso__PublicOrigin=https://example.com
```

The plugin inventory endpoint would then be:

```text
https://example.com/marketmafioso/api/inventory
```

## Backups

The receiver state lives in one SQLite database. Back up the configured `MarketMafioso__DatabasePath` file while the service is stopped, or use SQLite's online backup tooling if you need hot backups.

The original uploaded JSON is intentionally pruned after `RawJsonRetentionCount` snapshots. Structured inventory rows remain until `SnapshotRetentionCount` pruning removes older snapshots.

## Smoke Test

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri http://localhost:5088/health
```

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5088/inventory `
  -Headers @{ "X-Api-Key" = "<client-api-key>" } `
  -ContentType application/json `
  -Body (Get-Content docs/samples/inventory-report.sample.json -Raw)
```

The ingest response should include a dashboard URL.
