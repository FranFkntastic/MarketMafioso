# Workshop Host Settings Reference

Workshop Host is the private backend runtime for MarketMafioso. It still uses receiver-era routes, image names, and setting file names for compatibility. It reads settings from environment variables. In the Docker quick-start bundle, those variables live in `config/marketmafioso.env`.

Do not share this file publicly. It contains the plugin API key and dashboard bootstrap password.

## Core Server Settings

### `ASPNETCORE_URLS`

Example:

```text
ASPNETCORE_URLS=http://0.0.0.0:8080
```

This tells Workshop Host which address to listen on inside the container. The Docker bundle maps host port `5088` to container port `8080`, so most users should not change this.

Why it matters: if this does not match the Docker port mapping, the container can start but the dashboard will not be reachable.

### `MarketMafioso__PublicOrigin`

Example:

```text
MarketMafioso__PublicOrigin=http://localhost:5088
```

This is the public browser address for Workshop Host. The server uses it when it returns dashboard links after a plugin upload.

Why it matters: if this points to the wrong address, uploads may still work but links shown by the plugin can open the wrong site.

### `MarketMafioso__BasePath`

Example:

```text
MarketMafioso__BasePath=
```

Leave this blank for normal local installs. Use it only when an advanced reverse proxy hosts Workshop Host under a path such as `/marketmafioso`.

Why it matters: Workshop Host uses this to understand URLs when it is not hosted at the root of a domain.

### `MarketMafioso__StorageLabel`

Example:

```text
MarketMafioso__StorageLabel=self-hosted Workshop Host storage
```

This is a friendly label shown in server-generated output.

Why it matters: it helps identify which Workshop Host stored a report when you run more than one environment.

## Plugin API Key Settings

### `MarketMafioso__RequireApiKey`

Example:

```text
MarketMafioso__RequireApiKey=true
```

This requires the plugin to send the shared API key when it uploads inventory data.

Why it matters: keep this enabled for any Workshop Host you care about. Turning it off allows uploads without the shared key.

### `MarketMafioso__ClientApiKey`

Example:

```text
MarketMafioso__ClientApiKey=<generated key>
```

This is the shared secret between the plugin and Workshop Host. The same value must be pasted into the plugin's Client API Key setting.

Why it matters: if it does not match, the plugin cannot send reports. Treat it like a password.

### `MarketMafioso__PreviousClientApiKey`

Example:

```text
MarketMafioso__PreviousClientApiKey=
```

This optional value lets you temporarily accept an older key while rotating to a new one.

Why it matters: it prevents lockouts during key rotation. Leave it blank unless you are deliberately changing keys.

### Scoped Machine Keys

Examples:

```text
MarketMafioso__InventoryWriteApiKey=
MarketMafioso__InventoryReadApiKey=
MarketMafioso__CraftQuoteApiKey=
MarketMafioso__AcquisitionQueueApiKey=
MarketMafioso__DiagnosticsReadApiKey=
MarketMafioso__AutomationRunApiKey=
```

These optional keys narrow a machine client to one Workshop Host scope. Leave them blank for normal self-hosting; `MarketMafioso__ClientApiKey` remains the compatibility key for every implemented non-dashboard machine route.

Scope mapping:

- `InventoryWriteApiKey`: inventory upload routes.
- `InventoryReadApiKey`: machine report read routes.
- `CraftQuoteApiKey`: `/api/craft/appraise`.
- `AcquisitionQueueApiKey`: acquisition queue plugin routes.
- `DiagnosticsReadApiKey`: machine diagnostics routes when exposed.
- `AutomationRunApiKey`: reserved for future automation routes.

Any valid scoped machine key may read `/api/capabilities` so a narrow client can discover whether its feature is available. A `CraftQuoteApiKey` cannot read inventory reports or drive acquisition queues.

## Database And Retention Settings

### `MarketMafioso__DatabasePath`

Example:

```text
MarketMafioso__DatabasePath=/data/marketmafioso.db
```

This is where Workshop Host stores its SQLite database inside the container. The Docker bundle maps `/data` to `release/self-host/data/marketmafioso/` on your machine.

Why it matters: this database is the durable Workshop Host history. Back up the mapped host folder, not only the container.

### `MarketMafioso__RawJsonRetentionCount`

Example:

```text
MarketMafioso__RawJsonRetentionCount=20
```

This controls how many original uploaded JSON payloads are kept for diagnostics.

Why it matters: raw JSON is useful for troubleshooting but can grow over time. Lower values save space; higher values preserve more debug history.

### `MarketMafioso__SnapshotRetentionCount`

Example:

```text
MarketMafioso__SnapshotRetentionCount=500
```

This controls how many structured inventory snapshots are kept.

Why it matters: this is the main inventory history limit. Higher values keep more history and use more disk space.

### `MarketMafioso__DiagnosticsRetentionCount`

Example:

```text
MarketMafioso__DiagnosticsRetentionCount=5000
```

This controls how many diagnostic events are retained.

Why it matters: diagnostics help troubleshoot failed uploads, dashboard behavior, and Workshop Host state. Higher values are useful while testing, but they keep more rows in the database.

## Dashboard Login Settings

### `MarketMafioso__RequireDashboardAuth`

Example:

```text
MarketMafioso__RequireDashboardAuth=true
```

This requires browser users to log in before viewing the dashboard.

Why it matters: keep this enabled unless Workshop Host is only reachable on a fully trusted local machine.

### `MarketMafioso__DashboardBootstrapUsername`

Example:

```text
MarketMafioso__DashboardBootstrapUsername=marketmafioso
```

This is the username used to create the first dashboard account when the database has no dashboard users.

Why it matters: it is only used for initial account creation. Changing it later does not rename an existing account.

### `MarketMafioso__DashboardBootstrapPassword`

Example:

```text
MarketMafioso__DashboardBootstrapPassword=<generated password>
```

This is the password used to create the first dashboard account when the database has no dashboard users.

Why it matters: save it somewhere private after setup. Changing it later does not reset an existing dashboard user's password.

### `MarketMafioso__DashboardSessionMinutes`

Example:

```text
MarketMafioso__DashboardSessionMinutes=720
```

This controls how long a browser dashboard session stays valid before requiring login again.

Why it matters: shorter sessions are safer on shared machines; longer sessions are more convenient on a private machine.

## Changing Settings Safely

1. Stop the Workshop Host container.
2. Back up `config/marketmafioso.env` and `data/`.
3. Edit `config/marketmafioso.env`.
4. Start Workshop Host again.
5. Check `/health`.
6. Send a test report from the plugin.

If a setting change breaks Workshop Host, restore the previous `config/marketmafioso.env` and restart the container.
