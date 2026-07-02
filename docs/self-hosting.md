# Self-Hosting the MarketMafioso Receiver

MarketMafioso's receiver is a private ASP.NET Core service with a bundled dashboard and a local SQLite database. It is meant for self-hosted or small trusted deployments, not public multi-user hosting.

The receiver enables persistent inventory snapshots, the inventory browser, and diagnostics. The in-game plugin can still run without it, but server-backed inventory history and dashboard features will be unavailable.

## Requirements

- Docker with Docker Compose, or .NET 10 for direct hosting.
- A random client API key shared between the plugin and receiver.
- A dashboard username/password for browser login.
- Persistent storage for the SQLite database.

For Windows users who do not already have Docker, install Docker Desktop first. Docker's official Windows install guide is:

```text
https://docs.docker.com/desktop/setup/install/windows-install/
```

Start Docker Desktop before running receiver setup, then confirm Docker is available:

```powershell
docker --version
docker compose version
```

## Quick Docker Setup

The easiest path is the release bundle under `release/self-host/`. It uses the prebuilt server image instead of building the ASP.NET app locally.

```powershell
cd release/self-host
.\scripts\Install-MarketMafiosoReceiver.ps1
```

The installer wizard generates `config/marketmafioso.env`, pulls the server image, starts the container, checks `/health`, and prints the plugin endpoint/API key.

To update later:

```powershell
cd release/self-host
.\scripts\Update-MarketMafiosoReceiver.ps1
```

The update script backs up the SQLite database, pulls the latest image, and restarts the container.

## Manual Docker Setup

1. Copy the environment sample and replace every placeholder secret:

```powershell
Copy-Item release/self-host/config/marketmafioso.env.example release/self-host/config/marketmafioso.env
```

2. Edit `config/marketmafioso.env`:

```text
MarketMafioso__ClientApiKey=<random-client-key>
MarketMafioso__DashboardBootstrapUsername=marketmafioso
MarketMafioso__DashboardBootstrapPassword=<random-dashboard-password>
```

3. Start the receiver from the repo-local compose file:

```powershell
docker compose -f release/self-host/config/compose.yaml up -d
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

The release-bundle Docker path stores SQLite data under `release/self-host/data/marketmafioso/` on the host.

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
MarketMafioso__RequireDashboardAuth=true
MarketMafioso__DashboardBootstrapUsername=<dashboard-user>
MarketMafioso__DashboardBootstrapPassword=<dashboard-password>
```

The bootstrap dashboard user is created only when the receiver database has no dashboard users. Changing the bootstrap password later does not rotate an existing user's password.

For setting-by-setting explanations, see:

```text
docs/receiver-settings.md
```

## Advanced Hosting

Reverse proxy, HTTPS, path-prefix, manual Docker, and direct .NET hosting notes live in `docs/receiver-advanced-configuration.md`.

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
