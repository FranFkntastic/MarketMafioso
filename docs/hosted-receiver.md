# Hosted Workshop Host

MarketMafioso can send inventory snapshots to a hosted or self-hosted ASP.NET Workshop Host instead of requiring a local backend during normal use. The current deployed environment is still named the dev receiver in scripts and infrastructure; it remains optional for ordinary plugin use.

## Environments

The Workshop Host API is environment-scoped, not Craft Architect branch-scoped.

```text
Dev receiver:        https://dev.xivcraftarchitect.com/marketmafioso/api/inventory
Production receiver: not deployed yet
Local fallback:      http://localhost:8080/inventory
```

Stored snapshots are local to the Workshop Host environment. A snapshot sent to dev is not visible in production, and a production snapshot is not visible in dev unless it is explicitly exported and imported later.

MarketMafioso does not provide public multi-user inventory hosting. Users who want their own backend can run the released Workshop Host package themselves and manage their own service, domain, TLS, and backups.

## Plugin Configuration

The plugin settings window has endpoint preset buttons:

- `Local Receiver`
- `Dev VPS`
- `Production VPS (future)`

The URL remains editable. The plugin does not change existing saved URLs automatically.

Hosted Workshop Host environments require a client API key for plugin-to-server traffic. `MarketMafioso__ClientApiKey` bootstraps the receiver; after sign-in, use dashboard **Settings > Authentication** to issue a managed plugin key or a narrower Craft Architect key. Managed keys can be revoked independently without editing server configuration or restarting the receiver.

## Server Configuration

Run hosted Workshop Host behind Caddy with these environment variables:

```text
ASPNETCORE_URLS=http://127.0.0.1:5088
MarketMafioso__RequireApiKey=true
MarketMafioso__ClientApiKey=<secret>
MarketMafioso__PreviousClientApiKey=<optional-previous-secret>
MarketMafioso__BasePath=/marketmafioso
MarketMafioso__PublicOrigin=https://dev.xivcraftarchitect.com
MarketMafioso__StorageLabel=dev receiver storage
MarketMafioso__DatabasePath=/srv/craftarchitect/data/marketmafioso/dev/marketmafioso.db
MarketMafioso__RawJsonRetentionCount=20
MarketMafioso__InventoryHistoryRetentionPerCharacter=100
MarketMafioso__RequireDashboardAuth=true
MarketMafioso__DashboardBootstrapUsername=marketmafioso
MarketMafioso__DashboardBootstrapPassword=<dashboard-password>
```

`/health` remains public for uptime checks. Inventory ingestion, `/api/capabilities`, and `/api/reports...` machine-read routes require the client key. Browser dashboard routes use app-managed login sessions backed by the receiver SQLite database.

Hosted Workshop Host exposes `/api/capabilities` for feature discovery. It advertises `craft.appraise` only while a valid loopback Craft Architect API endpoint is configured; absent capability means the quote service is unavailable, not that MMF should compute a quote itself.

The dev dashboard username is fixed to `marketmafioso`; the password is stored in GitHub Actions as `MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD`. Bootstrap credentials create the first local dashboard admin user only when no dashboard users exist.

## Dev VPS Deployment

The `Deploy MarketMafioso Dev Receiver to VPS` GitHub Actions workflow publishes `MarketMafioso.Server` as a self-contained Linux app and deploys it to the dev Workshop Host environment:

```text
https://dev.xivcraftarchitect.com/marketmafioso/
```

Use the server-specific helper when you want to force a backend deployment and watch the smoke checks from PowerShell:

```powershell
.\src\MarketMafioso\tools\Deploy-ServerDev.ps1
```

The helper triggers the GitHub Actions workflow for `main`, waits for it to complete, then checks the public health/dashboard routes. If local secret files exist under `%USERPROFILE%\.ssh`, it also smoke-tests authenticated dashboard access and inventory ingestion without printing the secrets. To deploy a non-default ref deliberately, pass `-Ref`:

```powershell
.\src\MarketMafioso\tools\Deploy-ServerDev.ps1 -Ref test/inventory-browser-vps
```

Required repository secrets:

```text
VPS_HOST
VPS_USER
VPS_SSH_PRIVATE_KEY
VPS_SSH_PORT
MARKETMAFIOSO_DEV_INGEST_API_KEY
MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD
```

Optional repository secrets:

```text
MARKETMAFIOSO_DEV_PREVIOUS_INGEST_API_KEY
MARKETMAFIOSO_DEV_READ_API_KEY
MARKETMAFIOSO_DEV_PREVIOUS_READ_API_KEY
```

The workflow installs or updates the `marketmafioso-dev` systemd service, stores dev data under `/srv/craftarchitect/data/marketmafioso/dev`, and configures the dev Caddy site for public health, API-key ingest/read routes, and proxied dashboard routes.

The server compiles independently of Craft Architect. The dev deployment points it at CA's environment-matched loopback appraisal endpoint, then smoke-tests capability discovery, authentication, schema validation, and a real quote.

Server deployment is intentionally separate from plugin deployment. A backend deploy updates the VPS Workshop Host only; it does not copy a DLL into Dalamud. Use `Deploy-PluginDev.ps1` when the in-game plugin needs to change too.

When you just want the tooling to choose based on the files you changed, use the changed-surface router:

```powershell
.\src\MarketMafioso\tools\Deploy-ChangedDev.ps1
```

It classifies committed, staged, unstaged, and untracked paths. Server paths run the server deploy, plugin paths run the plugin deploy, both surfaces run the explicit combined deploy, and docs/tooling-only changes do not deploy. If a path is ambiguous, the router stops and asks you to run the explicit server/plugin/both command.

## First-Time Setup

Generate a dev client API key and dashboard password outside the repo:

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$clientKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
Set-Content -LiteralPath "$env:USERPROFILE\.ssh\marketmafioso_dev_api_key.txt" -Value $clientKey -NoNewline
gh secret set MARKETMAFIOSO_DEV_INGEST_API_KEY --repo FranFkntastic/MarketMafioso --body $clientKey

$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$dashboardPassword = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
Set-Content -LiteralPath "$env:USERPROFILE\.ssh\marketmafioso_dashboard_password.txt" -Value $dashboardPassword -NoNewline
gh secret set MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD --repo FranFkntastic/MarketMafioso --body $dashboardPassword
```

Sign in to the dashboard and generate a MarketMafioso plugin key under **Settings > Authentication**, then paste that one-time secret into the plugin-wide `Settings` tab's `Client API Key` field. Keep the environment key as a recovery credential instead of copying it into every client.

## Caddy Shape

Use Caddy routing so plugin/API and dashboard traffic reach the app. The app handles dashboard login sessions.

```caddyfile
dev.xivcraftarchitect.com {
    handle /marketmafioso {
        reverse_proxy 127.0.0.1:5088
    }

    handle /marketmafioso/* {
        reverse_proxy 127.0.0.1:5088
    }
}
```

Keep the `/marketmafioso` prefix when proxying to the app. Workshop Host uses `MarketMafioso__BasePath=/marketmafioso` to route those requests correctly.

The deployed Caddy fragment is installed as `root:root` with mode `644` so the Caddy service user can import it during reload. Keep the runtime environment file at `600`; that file contains API keys.

## Data Storage

Workshop Host stores structured inventory data in SQLite:

```text
src/MarketMafioso.Server/data/marketmafioso.db
```

For the dev VPS, the database is:

```text
/srv/craftarchitect/data/marketmafioso/dev/marketmafioso.db
```

The receiver keeps one durable current inventory for every known character. Older semantic changes remain available through structured dashboard and API views up to `MarketMafioso__InventoryHistoryRetentionPerCharacter`, defaulting to `100` per character. Original incoming JSON is governed separately by `MarketMafioso__RawJsonRetentionCount`, defaulting to `20`; raw JSON routes return `410 Gone` with `raw_json_pruned` after that diagnostic payload is pruned.

On first startup after the SQLite migration, existing JSON files under `data/reports/*.json` are imported into the default local account. The import is idempotent and does not delete source JSON files.

## Testing

Smoke-test ingestion with:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri https://dev.xivcraftarchitect.com/marketmafioso/api/inventory `
  -Headers @{ "X-Api-Key" = "<secret>" } `
  -ContentType application/json `
  -Body (Get-Content docs/samples/inventory-report.sample.json -Raw)
```

Then open the dashboard through the Caddy-protected URL:

```text
https://dev.xivcraftarchitect.com/marketmafioso/
```

Use username `marketmafioso` and the dashboard password stored outside the repo.

Smoke-test capabilities with:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri https://dev.xivcraftarchitect.com/marketmafioso/api/capabilities `
  -Headers @{ "X-Api-Key" = "<secret>" }
```

The response should include inventory/read diagnostics capabilities. It includes `craft.appraise` only when the separate Craft Architect service is configured and reachable by policy.
