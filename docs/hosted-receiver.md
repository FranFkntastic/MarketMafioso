# Hosted Receiver

MarketMafioso can send inventory snapshots to a hosted ASP.NET receiver instead of requiring the local backend during normal use.

## Environments

The receiver API is environment-scoped, not Craft Architect branch-scoped.

```text
Dev receiver:        https://dev.xivcraftarchitect.com/api/marketmafioso/inventory
Production receiver: https://xivcraftarchitect.com/api/marketmafioso/inventory
Local fallback:      http://localhost:8080/inventory
```

Stored snapshots are local to the receiver environment. A snapshot sent to dev is not visible in production, and a production snapshot is not visible in dev unless it is explicitly exported and imported later.

## Plugin Configuration

The plugin settings window has endpoint preset buttons:

- `Local Receiver`
- `Dev VPS`
- `Production VPS`

The URL remains editable. The plugin does not change existing saved URLs automatically.

Hosted receivers require an API key. Set the same value in the plugin's `API Key` field and in the server environment variable below.

## Server Configuration

Run the hosted receiver behind Caddy with these environment variables:

```text
ASPNETCORE_URLS=http://127.0.0.1:5088
MarketMafioso__RequireApiKey=true
MarketMafioso__ApiKey=<secret>
MarketMafioso__BasePath=/api/marketmafioso
```

`/health` remains public for uptime checks. Inventory ingestion and `/api/reports...` routes require `X-Api-Key` when API key authentication is enabled.

The dashboard HTML is intended to be protected by Caddy Basic Auth. Do not expose it unauthenticated on the public internet.

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
MARKETMAFIOSO_DEV_API_KEY
```

The workflow installs or updates the `marketmafioso-dev` systemd service, stores dev data under `/srv/craftarchitect/data/marketmafioso/dev`, and configures the dev Caddy site for public health, inventory ingest, and `/api/reports...` API routes. The dashboard route is not opened by this workflow until Caddy Basic Auth is configured for it.

## Caddy Shape

Use Caddy routing so plugin/API traffic reaches the app with `X-Api-Key`, while dashboard pages require human Basic Auth.

```caddyfile
dev.xivcraftarchitect.com {
    @marketmafiosoApi {
        path /api/marketmafioso/inventory /api/marketmafioso/api/*
    }

    handle @marketmafiosoApi {
        reverse_proxy 127.0.0.1:5088
    }

    @marketmafiosoDashboard {
        path /api/marketmafioso /api/marketmafioso/ /api/marketmafioso/reports/*
    }

    handle @marketmafiosoDashboard {
        basicauth {
            <user> <hashed-password>
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
