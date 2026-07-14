# Workshop Host Settings Reference

The installer writes Workshop Host settings to:

```text
config\marketmafioso.env
```

Do not share this file publicly. It contains the plugin API key and dashboard bootstrap password.

## Core Server Settings

### `ASPNETCORE_URLS`

The address Workshop Host listens on inside Docker. Leave this as `http://0.0.0.0:8080` unless you also change the Docker port mapping.

### `MarketMafioso__PublicOrigin`

The browser address users should open for the dashboard. For local installs this is `http://localhost:5088`.

This matters because Workshop Host uses it when returning dashboard links to the plugin.

### `MarketMafioso__BasePath`

Blank for normal local installs. Advanced path-mounted hosting can set this to a path such as `/marketmafioso`.

### `MarketMafioso__AllowedOrigins__0`

Set this to the Craft Architect browser origin when CA and Workshop Host use different schemes, hosts, or ports. Add `__1`, `__2`, and so on for additional CA origins. Browser access remains protected by the client API key.

### `MarketMafioso__StorageLabel`

A friendly label for this Workshop Host storage. Useful if you run more than one host.

## Plugin API Key Settings

### `MarketMafioso__RequireApiKey`

Requires the plugin to send the shared key when uploading reports. Keep this `true`.

### `MarketMafioso__ClientApiKey`

The shared key pasted into the plugin's Client API Key setting. Treat it like a password.

### `MarketMafioso__PreviousClientApiKey`

Optional old key accepted during key rotation. Leave it blank unless you are changing keys.

### Scoped Machine Keys

Optional narrow keys for specific Workshop Host scopes:

```text
MarketMafioso__InventoryWriteApiKey=
MarketMafioso__InventoryReadApiKey=
MarketMafioso__CraftQuoteApiKey=
MarketMafioso__AcquisitionQueueApiKey=
MarketMafioso__DiagnosticsReadApiKey=
MarketMafioso__AutomationRunApiKey=
```

Most self-host installs should leave these blank and use `MarketMafioso__ClientApiKey`. Any scoped key can read `/api/capabilities`; feature routes still require their own scope. For example, `CraftQuoteApiKey` can call `/api/craft/appraise` but cannot read inventory reports.

## Database And Retention Settings

### `MarketMafioso__DatabasePath`

The SQLite database path inside Docker. In the bundle, `/data/marketmafioso.db` maps to:

```text
data\marketmafioso\marketmafioso.db
```

This database is the durable Workshop Host history.

### `MarketMafioso__RawJsonRetentionCount`

How many original uploaded JSON reports to keep for diagnostics.

### `MarketMafioso__SnapshotRetentionCount`

How many structured inventory snapshots to keep.

### `MarketMafioso__DiagnosticsRetentionCount`

How many diagnostic events to keep.

## Dashboard Login Settings

### `MarketMafioso__RequireDashboardAuth`

Requires browser login before viewing the dashboard. Keep this `true` unless Workshop Host is only reachable on a fully trusted local machine.

### `MarketMafioso__DashboardBootstrapUsername`

The username used to create the first dashboard account when the database has no dashboard users.

### `MarketMafioso__DashboardBootstrapPassword`

The password used to create the first dashboard account when the database has no dashboard users.

Changing the bootstrap username or password later does not rename or reset an existing dashboard account.

### `MarketMafioso__DashboardSessionMinutes`

How long a browser dashboard session stays valid before requiring login again.

## Changing Settings Safely

1. Stop Workshop Host.
2. Back up `config\marketmafioso.env` and `data\`.
3. Edit `config\marketmafioso.env`.
4. Start Workshop Host again.
5. Check `http://localhost:5088/health`.
6. Send a test report from the plugin.
