# Local Backend

MarketMafioso includes a small local ASP.NET backend for testing inventory report uploads before the production VPS is ready.

## Run

```powershell
dotnet run --project MarketMafioso.Server --urls http://localhost:8080
```

Set the plugin server URL to:

```text
http://localhost:8080/inventory
```

Then use `/mmf send` in game, or press `Send Report Now` in the Inventory Reporter tab.

## Local Dashboard

Open:

```text
http://localhost:8080/
```

The dashboard is a local control panel for received snapshots. It shows summary counts, links to each snapshot's HTML detail view and raw JSON, and lets you delete individual snapshots.
It also has a `Delete All` action for clearing the local snapshot store.

Useful JSON endpoints:

```text
GET  /health
POST /inventory
POST /api/inventory
GET  /api/reports
GET  /api/reports/latest
GET  /api/reports/{id}
DELETE /api/reports
DELETE /api/reports/{id}
```

Reports are stored as JSON files under `MarketMafioso.Server/data/reports/`. That folder is ignored by git.

## API Key

The local server accepts unauthenticated reports by default. To require the plugin's `X-Api-Key` header locally:

```powershell
$env:MarketMafioso__ApiKey = "local-dev-key"
dotnet run --project MarketMafioso.Server --urls http://localhost:8080
```

Set the same API key in the plugin UI before sending.

## Smoke Test

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:8080/inventory `
  -ContentType application/json `
  -Body (Get-Content docs/samples/inventory-report.sample.json -Raw)
```
