# Hosted Receiver

MarketMafioso can send inventory snapshots to a hosted ASP.NET receiver instead of requiring the local backend during normal use.

## Environments

The receiver API is environment-scoped, not Craft Architect branch-scoped.

```text
Dev receiver:        https://dev.xivcraftarchitect.com/api/marketmafioso/inventory
Production receiver: not deployed yet
Local fallback:      http://localhost:8080/inventory
```

Stored snapshots are local to the receiver environment. A snapshot sent to dev is not visible in production, and a production snapshot is not visible in dev unless it is explicitly exported and imported later.

## Plugin Configuration

The plugin settings window has endpoint preset buttons:

- `Local Receiver`
- `Dev VPS`
- `Production VPS (future)`

The URL remains editable. The plugin does not change existing saved URLs automatically.

Hosted receivers require an ingest API key. Set the same value in the plugin's `API Key` field and in `MarketMafioso__IngestApiKey` on the server. Do not use the optional read API key in the plugin.

## Server Configuration

Run the hosted receiver behind Caddy with these environment variables:

```text
ASPNETCORE_URLS=http://127.0.0.1:5088
MarketMafioso__RequireApiKey=true
MarketMafioso__IngestApiKey=<secret>
MarketMafioso__PreviousIngestApiKey=<optional-previous-secret>
MarketMafioso__ReadApiKey=<optional-read-secret>
MarketMafioso__PreviousReadApiKey=<optional-previous-read-secret>
MarketMafioso__BasePath=/api/marketmafioso
MarketMafioso__PublicOrigin=https://dev.xivcraftarchitect.com
MarketMafioso__StorageLabel=dev receiver storage
```

`/health` remains public for uptime checks. Inventory ingestion requires the ingest key. `/api/reports...` is optional machine-read API surface: it requires the read key when configured and fails closed when no read key is configured. Browser dashboard routes use Caddy Basic Auth instead.

The dashboard HTML is protected by Caddy Basic Auth. The dev username is fixed to `marketmafioso`; the password is stored in GitHub Actions as `MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD`.

## Dev VPS Deployment

The `Deploy MarketMafioso Dev Receiver to VPS` GitHub Actions workflow publishes `MarketMafioso.Server` as a self-contained Linux app and deploys it to the dev receiver:

```text
https://dev.xivcraftarchitect.com/api/marketmafioso/
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

The workflow installs or updates the `marketmafioso-dev` systemd service, stores dev data under `/srv/craftarchitect/data/marketmafioso/dev`, and configures the dev Caddy site for public health, API-key ingest/read routes, and Basic-Auth dashboard routes.

## First-Time Setup

Generate a dev ingest key and dashboard password outside the repo:

```powershell
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$ingestKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
Set-Content -LiteralPath "$env:USERPROFILE\.ssh\marketmafioso_dev_api_key.txt" -Value $ingestKey -NoNewline
gh secret set MARKETMAFIOSO_DEV_INGEST_API_KEY --repo FranFkntastic/MarketMafioso --body $ingestKey

$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$dashboardPassword = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
Set-Content -LiteralPath "$env:USERPROFILE\.ssh\marketmafioso_dashboard_password.txt" -Value $dashboardPassword -NoNewline
gh secret set MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD --repo FranFkntastic/MarketMafioso --body $dashboardPassword
```

Paste only the ingest key into the plugin's `API Key` field.

## Caddy Shape

Use Caddy routing so plugin/API traffic reaches the app with `X-Api-Key`, while dashboard pages require human Basic Auth.

```caddyfile
dev.xivcraftarchitect.com {
    @marketmafiosoApi {
        path /api/marketmafioso/health /api/marketmafioso/inventory /api/marketmafioso/api/inventory /api/marketmafioso/api/reports*
    }

    handle @marketmafiosoApi {
        reverse_proxy 127.0.0.1:5088
    }

    @marketmafiosoDashboard {
        path /api/marketmafioso /api/marketmafioso/ /api/marketmafioso/reports*
    }

    handle @marketmafiosoDashboard {
        basic_auth {
            marketmafioso <hashed-password>
        }
        reverse_proxy 127.0.0.1:5088
    }
}
```

Keep the `/api/marketmafioso` prefix when proxying to the app. The receiver uses `MarketMafioso__BasePath=/api/marketmafioso` to route those requests correctly.

## Data Storage

The receiver stores JSON files under:

```text
MarketMafioso.Server/data/reports/
```

For a VPS deployment, point the service working directory or content root at a durable deployment folder and back up that `data/reports` directory if snapshots matter.

## Testing

Smoke-test ingestion with:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri https://dev.xivcraftarchitect.com/api/marketmafioso/inventory `
  -Headers @{ "X-Api-Key" = "<secret>" } `
  -ContentType application/json `
  -Body (Get-Content docs/samples/inventory-report.sample.json -Raw)
```

Then open the dashboard through the Caddy-protected URL:

```text
https://dev.xivcraftarchitect.com/api/marketmafioso/
```

Use username `marketmafioso` and the dashboard password stored outside the repo.
