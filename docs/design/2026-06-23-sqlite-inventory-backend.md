# SQLite Inventory Backend Design and Implementation Notes

## Goal

Move the inventory receiver from one JSON file per snapshot to a durable SQLite database that stores unpacked inventory data, keeps the dashboard private, and reduces long-term disk growth.

This is a self-hosted/private receiver, not a public MarketMafioso hosting service. Other users who want a backend for Inventory Reporter can run the released server package themselves. Accounts in this design are local to one receiver instance: they own inventory data, dashboard users can access one or more local accounts, and public multi-tenant hosting remains out of scope.

## Current State

- `InventoryReportStore` writes each snapshot as a `StoredInventoryReport` JSON file under `data/reports/`.
- Dashboard listing and latest lookup enumerate all JSON files and deserialize each one.
- The server already has a parsed view model through `InventorySnapshotView` and `InventorySnapshotViewBuilder`.
- Hosted plugin/server traffic is protected by a client API key.
- Hosted dashboard pages are protected by Caddy Basic Auth.
- Machine-read report APIs use the same client API key.

## Product Boundaries

In scope:

- SQLite-backed snapshot storage.
- Structured inventory rows for snapshots, owners, bags, and item stacks.
- Basic receiver-local accounts and dashboard users for this private receiver.
- Account-scoped client API keys.
- Character-aware dashboard filtering within an account.
- Existing client API key behavior for plugin uploads.
- Raw original JSON retention for the newest 20 snapshots.
- One-time import of existing JSON snapshot files.
- A releasable server package that advanced users can self-host.

Out of scope:

- Public sign-up.
- OpenAI-hosted or project-hosted inventory storage for other users.
- Per-player hosted accounts on our infrastructure.
- Sharing inventory between users.
- OAuth, email, password reset, billing, quotas, or public abuse handling.
- Market-board, undercut, or sale-listing storage.

## Recommended Architecture

### Storage

Use SQLite through `Microsoft.Data.Sqlite`. Store the database under the existing durable data root:

```text
data/marketmafioso.db
```

For the VPS, keep the database under the existing service data path:

```text
/srv/craftarchitect/data/marketmafioso/dev/marketmafioso.db
```

Add configuration:

```text
MarketMafioso__DatabasePath=/srv/craftarchitect/data/marketmafioso/dev/marketmafioso.db
MarketMafioso__RawJsonRetentionCount=20
MarketMafioso__RequireDashboardAuth=true
MarketMafioso__DashboardBootstrapUsername=<admin-user>
MarketMafioso__DashboardBootstrapPassword=<admin-password>
```

If `DatabasePath` is not configured, derive it from the content root as `data/marketmafioso.db`.

### Tables

Use explicit schema migrations in app code. This server is small enough that a single `SqliteSchemaMigrator` is preferable to adding EF Core.

```sql
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at_utc TEXT NOT NULL
);

CREATE TABLE dashboard_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT NOT NULL,
    is_admin INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    disabled_at_utc TEXT NULL,
    last_login_at_utc TEXT NULL
);

CREATE TABLE accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    disabled_at_utc TEXT NULL
);

CREATE TABLE dashboard_user_accounts (
    dashboard_user_id INTEGER NOT NULL REFERENCES dashboard_users(id) ON DELETE CASCADE,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    is_default INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (dashboard_user_id, account_id)
);

CREATE TABLE ingest_keys (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    label TEXT NOT NULL,
    key_hash TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    disabled_at_utc TEXT NULL
);

CREATE TABLE characters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    character_name TEXT NOT NULL,
    home_world TEXT NULL,
    first_seen_at_utc TEXT NOT NULL,
    last_seen_at_utc TEXT NOT NULL,
    UNIQUE(account_id, character_name, home_world)
);

CREATE TABLE snapshots (
    id TEXT PRIMARY KEY,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    character_id INTEGER NULL REFERENCES characters(id) ON DELETE SET NULL,
    received_at_utc TEXT NOT NULL,
    api_key_label TEXT NULL,
    character_name TEXT NULL,
    home_world TEXT NULL,
    report_timestamp TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    source_plugin TEXT NOT NULL,
    plugin_version TEXT NOT NULL,
    generated_at_utc TEXT NOT NULL,
    raw_report_json TEXT NULL,
    raw_json_retained_at_utc TEXT NULL
);

CREATE TABLE inventory_owners (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
    owner_type TEXT NOT NULL,
    owner_name TEXT NOT NULL,
    retainer_id INTEGER NULL,
    last_updated TEXT NULL,
    sort_order INTEGER NOT NULL
);

CREATE TABLE inventory_bags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id INTEGER NOT NULL REFERENCES inventory_owners(id) ON DELETE CASCADE,
    bag_name TEXT NOT NULL,
    sort_order INTEGER NOT NULL
);

CREATE TABLE inventory_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bag_id INTEGER NOT NULL REFERENCES inventory_bags(id) ON DELETE CASCADE,
    item_id INTEGER NOT NULL,
    item_name TEXT NULL,
    quantity INTEGER NOT NULL,
    is_hq INTEGER NOT NULL,
    condition REAL NOT NULL,
    sort_order INTEGER NOT NULL
);

CREATE INDEX idx_snapshots_account_received_at ON snapshots(account_id, received_at_utc DESC);
CREATE INDEX idx_snapshots_character_received_at ON snapshots(character_id, received_at_utc DESC);
CREATE INDEX idx_inventory_owners_snapshot ON inventory_owners(snapshot_id, sort_order);
CREATE INDEX idx_inventory_bags_owner ON inventory_bags(owner_id, sort_order);
CREATE INDEX idx_inventory_items_bag ON inventory_items(bag_id, sort_order);
CREATE INDEX idx_inventory_items_item ON inventory_items(item_id);
```

`owner_type` should be constrained in application code to `player` or `retainer`.

### Account and Character Scope

Use one SQLite database per receiver instance. Inside that database, accounts own inventory data.

Default setup creates:

- One account named `Default`.
- One dashboard admin user.
- One account-scoped ingest key, supplied from existing ingest-key configuration.
- A link from the admin user to the default account.

Ingest keys belong to accounts. When a plugin uploads a snapshot, the server resolves the supplied ingest key to an account before writing any rows. This keeps the storage model ready for multiple local accounts without adding public hosted accounts.

Characters are not the security boundary. They are account-owned filter targets. The server should upsert a `characters` row from `character_name` and `home_world` on each snapshot, then store `snapshots.character_id`.

Dashboard behavior:

- Default to the dashboard user's default account.
- Default snapshot lists to the most recently seen character in that account, if one exists.
- Provide an all-characters view for account-wide inventory inspection.
- Never show snapshots from accounts the dashboard user is not linked to.

This keeps the normal UI focused on one character while preserving account-level workflows for alts, retainers, backups, and future item search.

### Raw JSON Retention

Store the original incoming report JSON in `snapshots.raw_report_json` only for the newest configured number of snapshots. Default:

```text
RawJsonRetentionCount = 20
```

After each successful save, run a retention query that keeps raw JSON for the newest 20 snapshots and sets `raw_report_json` and `raw_json_retained_at_utc` to `NULL` for older snapshots.

Older snapshots remain fully usable through structured views, summaries, and dashboard pages. Raw JSON endpoints should return an explicit `410 Gone` response with an error such as `raw_json_pruned` when the snapshot exists but raw JSON has been pruned.

### Snapshot Save Flow

Replace model-bound ingest with an explicit request-body parse so the server can preserve the original incoming JSON:

1. Read the request body as a string.
2. Deserialize it into `InventoryReport`.
3. Validate at least one player bag or retainer exists.
4. Start a SQLite transaction.
5. Insert one `snapshots` row.
6. Insert one player owner row and one retainer owner row per retainer.
7. Insert bag rows in incoming order.
8. Insert item rows in incoming order.
9. Apply raw JSON retention.
10. Commit.
11. Return the existing created summary contract.

If deserialization or validation fails, return `400` and do not write partial rows.

### Dashboard Accounts

Move dashboard Basic Auth from Caddy into the ASP.NET app so dashboard users and receiver-local accounts can live in SQLite.

Keep the user experience simple: the browser still shows a Basic Auth prompt. The server validates credentials against `dashboard_users`.

Rules:

- Dashboard routes require an active dashboard user when `RequireDashboardAuth=true`.
- `/health` remains public.
- Inventory ingest uses the client API key.
- `/api/reports...` machine-read routes use the same client API key.
- Dashboard users authenticate people and can access linked local accounts.
- Accounts own snapshots.
- No self-registration.
- No password reset flow.

Bootstrap:

- On startup, run migrations.
- If no dashboard users exist and `RequireDashboardAuth=true`, require both `DashboardBootstrapUsername` and `DashboardBootstrapPassword`.
- Create the `Default` account if no accounts exist.
- Create the first admin user from those bootstrap settings.
- Link the first admin user to the `Default` account and mark it as the user's default account.
- Associate the configured client API key with the `Default` account.
- If users already exist, ignore bootstrap credentials.
- If dashboard auth is required but no user exists and bootstrap settings are missing, fail startup with a clear error.

Hash passwords with PBKDF2 using a per-user salt and an encoded hash format that includes algorithm, iterations, salt, and hash. Do not store plaintext passwords or reusable Basic Auth hashes in the database.

The Caddy fragment should stop applying `basic_auth` to MarketMafioso dashboard paths once the app handles dashboard auth.

### Existing JSON Import

Add an importer for files under `data/reports/*.json`.

Import behavior:

- Import valid `StoredInventoryReport` files into SQLite.
- Preserve the original file contents as raw JSON only for the newest 20 imported snapshots.
- Use the stored report fields to populate structured rows.
- Skip duplicate snapshot IDs already present in SQLite.
- Record corrupt import files in logs with full paths and clear exception messages.
- Do not delete source JSON files automatically in the first implementation.

After a successful manual backup, a later cleanup task can archive or delete imported JSON files.

### Self-Hosted Server Package

Publish the server as a release artifact for users who want to run their own inventory receiver.

The package should be a standard `dotnet publish` output for `MarketMafioso.Server`, with docs for:

- Running locally.
- Running behind a reverse proxy.
- Setting the client API key.
- Bootstrapping the first dashboard admin user.
- Backing up the SQLite database.
- Understanding that the raw original JSON is retained only for the newest configured snapshot count.

The package does not need a desktop installer, auto-updater, hosted control plane, or public user registration. It is acceptable for self-hosters to manage their own service wrapper, domain, TLS, and backups.

### API Compatibility

Keep:

```text
GET /api/reports
GET /api/reports/latest
GET /api/reports/{id}/view
GET /reports/{id}
GET /reports/{id}/json
GET /reports/latest/json
DELETE /api/reports/{id}
DELETE /api/reports
POST /reports/{id}/delete
POST /reports/delete-all
```

Behavior changes:

- Summary/list endpoints should read from SQLite.
- Parsed view endpoints should build from SQLite rows instead of deserializing stored files.
- Raw JSON endpoints return raw retained JSON when available.
- Raw JSON endpoints return `410 Gone` for snapshots whose raw JSON has been pruned.
- Delete endpoints remove snapshots from SQLite with cascade deletes.

## Implementation Plan

### Task 1: Add SQLite Infrastructure

Files:

- Modify `MarketMafioso.Server/MarketMafioso.Server.csproj`.
- Create `MarketMafioso.Server/Sqlite/SqliteConnectionFactory.cs`.
- Create `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs`.

Steps:

1. Add `Microsoft.Data.Sqlite`.
2. Add a connection factory that resolves `MarketMafioso:DatabasePath`.
3. Enable foreign keys per connection with `PRAGMA foreign_keys = ON`.
4. Add schema migration `1` with the account, auth, snapshot, owner, bag, item, and index tables above.
5. Register the factory and migrator in `Program.cs`.
6. Run migrations before mapping endpoints.
7. Add focused tests that create a temp database and verify tables exist.

### Task 2: Replace File Store With SQLite Store

Files:

- Replace `MarketMafioso.Server/InventoryReportStore.cs`.
- Add small private mapping helpers if the file grows too large.

Steps:

1. Keep the public store method names where practical: `SaveAsync`, `ListSummariesAsync`, `GetLatestAsync`, `GetAsync`, `DeleteAsync`, and `DeleteAllAsync`.
2. Change `SaveAsync` to accept the resolved account id, raw report JSON, and parsed `InventoryReport`.
3. Upsert the character for the account based on character name and home world.
4. Insert snapshot, owner, bag, and item rows in one transaction.
5. Scope summary/list/latest/get/delete queries by account.
6. Compute summaries from SQL aggregate queries.
7. Rebuild `StoredInventoryReport` from SQLite rows for existing call sites.
8. Add tests for account isolation, character filtering, save, list, latest, get, delete, delete-all, and cascade behavior.

### Task 3: Preserve and Prune Raw JSON

Files:

- Modify `MarketMafioso.Server/InventoryReportStore.cs`.
- Modify `MarketMafioso.Server/Program.cs`.

Steps:

1. Read inventory POST bodies manually before deserialization.
2. Store the original incoming JSON text for new snapshots.
3. Add raw retention count configuration with default `20`.
4. After save, clear raw JSON for snapshots outside the newest retained set.
5. Update raw JSON endpoints to return retained raw text.
6. Return `410 Gone` when a snapshot exists but raw JSON was pruned.
7. Add tests proving only the newest configured count retains raw JSON.

### Task 4: Add Dashboard Account Auth

Files:

- Create `MarketMafioso.Server/Auth/AccountStore.cs`.
- Create `MarketMafioso.Server/Auth/DashboardUserStore.cs`.
- Create `MarketMafioso.Server/Auth/IngestKeyStore.cs`.
- Create `MarketMafioso.Server/Auth/DashboardPasswordHasher.cs`.
- Create `MarketMafioso.Server/Auth/DashboardBasicAuthMiddleware.cs`.
- Modify `MarketMafioso.Server/Program.cs`.
- Modify `.github/workflows/deploy-vps-marketmafioso-dev.yml`.
- Modify `docs/hosted-receiver.md`.

Steps:

1. Add account CRUD methods needed for default-account bootstrap and dashboard account lookup.
2. Add dashboard user CRUD methods needed for bootstrap and credential validation.
3. Add ingest-key hash storage and lookup methods.
4. Add PBKDF2 password hashing and verification.
5. Add startup bootstrap behavior for the default account, first admin user, and configured ingest key.
6. Add middleware that challenges dashboard routes with `WWW-Authenticate: Basic`.
7. Keep `/health` public, keep account-scoped client API key auth for inventory posts and machine APIs.
8. Remove Caddy `basic_auth` for the receiver dashboard routes in the deploy workflow.
9. Add deployment secrets for dashboard bootstrap username and password.
10. Add tests for missing credentials, valid credentials, disabled users, account links, ingest key account resolution, and protected dashboard routes.

### Task 5: Import Existing JSON Files

Files:

- Create `MarketMafioso.Server/Migration/JsonSnapshotImporter.cs`.
- Modify `Program.cs`.
- Add tests under `MarketMafioso.Server.Tests`.

Steps:

1. Enumerate existing `data/reports/*.json` files.
2. Deserialize each as `StoredInventoryReport`.
3. Import valid snapshots into the default account if their IDs do not already exist.
4. Upsert characters during import.
5. Preserve raw file contents only for the newest retained snapshots.
6. Log corrupt files clearly without deleting them.
7. Add an idempotency test that imports the same directory twice without duplicating rows.

### Task 6: Update Documentation and Verification

Files:

- Modify `docs/hosted-receiver.md`.
- Modify `docs/local-backend.md`.
- Modify `README.md`.

Steps:

1. Document database location, backup path, and raw JSON retention.
2. Document receiver-local accounts, dashboard users, account-scoped client API keys, and character filtering.
3. Document dashboard account bootstrap settings.
4. Document that the server can be self-hosted from release artifacts.
5. Document that hosted public multi-user service is out of scope.
6. Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
dotnet format "MarketMafioso.sln" --verify-no-changes
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
dotnet test "MarketMafioso.sln" -c Debug -v minimal
```

## Risks

- Moving Basic Auth from Caddy into the app changes the hosted auth boundary. The deployment workflow and smoke tests need to prove dashboard unauthenticated requests still return `401`.
- Raw JSON pruning changes old raw JSON routes. Returning `410 Gone` keeps this explicit instead of silently reconstructing a payload and pretending it is original.
- SQLite writes should be transaction-bound. Partial snapshot writes must not be possible.
- Import should be repeatable. A failed deploy should not duplicate imported snapshots on restart.
- SQLite file growth may not shrink immediately after raw JSON pruning. The database will reuse freed pages; if physical shrink matters, add a manual maintenance command later.

## Deferred Follow-Ups

- Dashboard UI for adding, disabling, or rotating dashboard users.
- Manual archive/delete command for imported JSON files after backup.
- GitHub Actions release packaging for the self-hosted server artifact.
- Search and aggregate item views backed by the new item table.
- Snapshot retention by age or count.
- Public multi-user hosting, account ownership, quotas, and user-scoped ingest keys.
