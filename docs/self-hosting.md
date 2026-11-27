# Self-Hosting Workshop Host

Workshop Host is MarketMafioso's private ASP.NET Core backend with a bundled dashboard and a local SQLite database. It is meant for self-hosted or small trusted deployments, not public multi-user hosting.

Workshop Host enables persistent inventory snapshots, the inventory browser, Market Acquisition work orders, diagnostics, and private suite integrations. The in-game plugin, including Market Acquisition buying runs, works without it; server-backed inventory history, work orders, and dashboard features need it.

The current package still keeps receiver-era names in scripts, routes, Docker image, and some setting filenames for compatibility.

Craft Architect craft quotes run inside Workshop Host: there is no separate Craft Architect service to install, and `craft.appraise` is always available to keys with the `craft:quote` scope.

## Requirements

- Docker with Docker Compose, or .NET 10 for direct hosting.
- A random client API key shared between the plugin and Workshop Host.
- A dashboard username/password for browser login.
- Persistent storage for the SQLite database.
- Outbound internet access to Universalis and Garland Tools for craft quotes and item search (see [Outside Services](#outside-services)).

## Outside Services

Workshop Host runs Craft Architect's appraisal engine and item search in-process. It contacts only these outside services:

- **Universalis** (`universalis.app`) for market prices used in craft quotes and listing diagnostics.
- **Garland Tools** (`www.garlandtools.org`) for item names, recipes, and vendor data used in craft quotes and dashboard item search.

Craft quotes and dashboard item search need outbound internet access to those two services. Workshop Host never sends requests to any Craft Architect server; a quote's plan link only opens the Craft Architect web app in your browser, which then reads the plan snapshot back from your Workshop Host.

For Windows users who do not already have Docker, install Docker Desktop first. Docker's official Windows install guide is:

```text
https://docs.docker.com/desktop/setup/install/windows-install/
```

Start Docker Desktop before running Workshop Host setup, then confirm Docker is available:

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

The installer asks no questions. It generates `config/marketmafioso.env` for local use (dashboard user `marketmafioso`), pulls the server image, starts the container, checks `/health`, then signs in with the generated dashboard password and prints a one-time plugin connection code. The API key is printed as a fallback. Use `-DashboardUsername` and `-PublicOrigin` to change the defaults.

After health passes, the installer checks Workshop Host capabilities, reports whether the optional CA service is configured, confirms unauthenticated quote requests are rejected, and confirms the quote endpoint validates schema shape with the generated client key.

To update later:

```powershell
cd release/self-host
.\scripts\Update-MarketMafiosoReceiver.ps1
```

The update script backs up the SQLite database, pulls the latest image, restarts the container, and runs the same health plus Workshop Host quote smoke checks.

## Manual Docker Setup

1. Copy the environment sample:

```powershell
Copy-Item release/self-host/config/marketmafioso.env.example release/self-host/config/marketmafioso.env
```

2. Replace the two secrets in `config/marketmafioso.env` with long random strings:

```text
MarketMafioso__ClientApiKey=<random-client-key>
MarketMafioso__DashboardBootstrapPassword=<random-dashboard-password>
```

3. Start Workshop Host:

```powershell
docker compose -f release/self-host/config/compose.yaml up -d
```

Sign in to `http://localhost:5088/` as `marketmafioso`, open **Settings > Authentication** and press **Connect game client**. In game, type `/mmf`, open **Settings > General / Server Connection**, paste the code into **Connect with code** and press **Connect**. Codes work once and expire after 10 minutes.

To use the key instead, open **Manual setup** on that plugin page and enter `http://localhost:5088/inventory` as the Server URL and `MarketMafioso__ClientApiKey` as the MarketMafioso Client Key.

The release-bundle Docker path stores SQLite data under `release/self-host/data/marketmafioso/` on the host.

## Direct .NET Hosting

Publish the server:

```powershell
.\src\MarketMafioso\tools\Publish-ServerRelease.ps1
```

Direct source publishing does not require a Craft Architect checkout: the appraisal engine comes from the `Gooseworks.FFXIVCraftArchitect.Core` package on nuget.org.

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
MarketMafioso__InventoryWriteApiKey=<optional-inventory-write-only-key>
MarketMafioso__InventoryReadApiKey=<optional-inventory-read-only-key>
MarketMafioso__CraftQuoteApiKey=<optional-craft-quote-only-key>
MarketMafioso__DiagnosticsReadApiKey=<optional-diagnostics-read-only-key>
MarketMafioso__BasePath=<path-prefix-or-empty>
MarketMafioso__PublicOrigin=<public-origin-for-dashboard-and-plan-links>
MarketMafioso__CraftArchitectAppOrigin=https://xivcraftarchitect.com
MarketMafioso__DatabasePath=<sqlite-file-path>
MarketMafioso__EnableMarketAcquisition=true
MarketMafioso__RawJsonRetentionCount=20
MarketMafioso__InventoryHistoryRetentionPerCharacter=100
MarketMafioso__DiagnosticsRetentionCount=5000
MarketMafioso__RequireDashboardAuth=true
MarketMafioso__DashboardBootstrapUsername=<dashboard-user>
MarketMafioso__DashboardBootstrapPassword=<dashboard-password>
```

The bootstrap dashboard user is created only when the Workshop Host database has no dashboard users. Changing the bootstrap password later does not rotate an existing user's password.

Scoped machine keys are optional. Most installs should leave them blank and use `MarketMafioso__ClientApiKey`, which remains the compatibility key for all implemented non-dashboard machine routes.

For setting-by-setting explanations, see:

```text
docs/receiver-settings.md
```

## Advanced Hosting

Reverse proxy, HTTPS, path-prefix, manual Docker, and direct .NET hosting notes live in `docs/receiver-advanced-configuration.md`.

## Backups

Workshop Host state lives in one SQLite database. Back up the configured `MarketMafioso__DatabasePath` file while the service is stopped, or use SQLite's online backup tooling if you need hot backups.

The same directory also holds `craft-appraisal-plans/` (priced plan snapshots behind quote plan links) and `craft-appraisal-cache/` (cached market prices). Back up the plans folder if you want old plan links to keep opening; the cache can be deleted at any time. Workshop Host does not prune old plan snapshots; each is a small JSON file.

The receiver keeps one durable current inventory for every known character. `InventoryHistoryRetentionPerCharacter` bounds older semantic changes independently, while `RawJsonRetentionCount` separately bounds original uploaded JSON used for diagnostics.

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

Check Workshop Host capabilities:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri http://localhost:5088/api/capabilities `
  -Headers @{ "X-Api-Key" = "<client-api-key>" }
```

The response lists `craft.appraise` for any key with the `craft:quote` scope, including the client API key. Acquisition Workbench uses it after you enable Craft Architect quotes in plugin settings.

The quote endpoint exists at:

```text
http://localhost:5088/api/craft/appraise
```

Workshop Host computes the quote itself, using Universalis and Garland Tools data. A quote's `planUrl` opens the Craft Architect web app (`MarketMafioso__CraftArchitectAppOrigin`), which reads the priced plan from this host at `/api/craft/plans/<id>`. That snapshot route needs no API key; the 64-character plan id is the access token.

The installer and updater smoke-test quote auth and request validation without doing a real appraisal, so setup is not blocked by transient upstream recipe or pricing data.
