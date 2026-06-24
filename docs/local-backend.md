# Receiver Backend

MarketMafioso includes a small ASP.NET receiver for inventory report uploads. The hosted VPS receiver is the normal direction for day-to-day use; the local receiver remains useful for offline debugging and development.

Hosted receiver notes live in [hosted-receiver.md](hosted-receiver.md).

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

The dashboard is a local control panel for received snapshots. It shows summary counts, links to each snapshot's HTML detail view, and lets you delete individual snapshots.
It also links to an Inventory Browser for the latest structured inventory and a Diagnostics view for retained raw JSON payloads.
It has a `Delete All` action for clearing the local snapshot store.
Snapshot details include parsed player/retainer inventory tables and metadata when the plugin supplies it.

Useful JSON endpoints:

```text
GET  /health
POST /inventory
POST /api/inventory
GET  /api/reports
GET  /api/reports/latest
GET  /api/reports/{id}
GET  /api/reports/{id}/view
DELETE /api/reports
DELETE /api/reports/{id}
```

Reports are stored in SQLite at `MarketMafioso.Server/data/marketmafioso.db` by default. Existing JSON files under `MarketMafioso.Server/data/reports/` are imported on startup and left in place.

The original incoming JSON is retained only for the newest 20 snapshots by default. Older snapshots remain available through parsed dashboard/API views until the structured snapshot retention limit is reached, while raw JSON routes return `410 Gone` once the original JSON has been pruned.

Structured snapshots are retained for the newest 500 snapshots by default. Override this with:

```powershell
$env:MarketMafioso__SnapshotRetentionCount = "500"
```

## API Key

The local server accepts unauthenticated reports by default. To require the plugin's `X-Api-Key` header locally:

```powershell
$env:MarketMafioso__ApiKey = "local-dev-key"
dotnet run --project MarketMafioso.Server --urls http://localhost:8080
```

Set the same API key in the plugin UI before sending.

## Dashboard Auth

The local dashboard is unauthenticated by default. To enable app-managed Basic Auth for dashboard pages:

```powershell
$env:MarketMafioso__RequireDashboardAuth = "true"
$env:MarketMafioso__DashboardBootstrapUsername = "admin"
$env:MarketMafioso__DashboardBootstrapPassword = "change-me"
dotnet run --project MarketMafioso.Server --urls http://localhost:8080
```

The bootstrap user is created only when no dashboard users exist. Dashboard users are local to this receiver instance.

## Smoke Test

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:8080/inventory `
  -ContentType application/json `
  -Body (Get-Content docs/samples/inventory-report.sample.json -Raw)
```
