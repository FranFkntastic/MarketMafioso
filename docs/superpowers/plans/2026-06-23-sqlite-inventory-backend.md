# SQLite Inventory Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace JSON-file inventory snapshot storage with account-scoped SQLite storage while keeping raw original JSON for the newest 20 snapshots and preserving current receiver endpoints.

**Architecture:** The ASP.NET receiver owns a small SQLite database with explicit migrations, account-scoped ingest keys, dashboard users, characters, snapshots, owners, bags, and item rows. `InventoryReportStore` remains the main persistence facade so endpoints can evolve without spreading SQL through `Program.cs`.

**Tech Stack:** C# 12, ASP.NET Core Minimal APIs, `Microsoft.Data.Sqlite`, xUnit, `WebApplicationFactory`.

---

### Task 1: SQLite Schema and Bootstrap

**Files:**
- Modify: `MarketMafioso.Server/MarketMafioso.Server.csproj`
- Create: `MarketMafioso.Server/Sqlite/SqliteConnectionFactory.cs`
- Create: `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs`
- Create: `MarketMafioso.Server/Auth/DashboardPasswordHasher.cs`
- Create: `MarketMafioso.Server/Auth/ReceiverBootstrapper.cs`
- Test: `MarketMafioso.Server.Tests/SqliteSchemaMigratorTests.cs`

- [ ] **Step 1: Write failing schema/bootstrap tests**

Create tests that use a temp database path, run migrations/bootstrap, and assert that `accounts`, `dashboard_users`, `dashboard_user_accounts`, `ingest_keys`, `characters`, `snapshots`, `inventory_owners`, `inventory_bags`, and `inventory_items` exist. Assert bootstrap creates one `Default` account, one admin user, one user-account link, and one ingest key when configured.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~SqliteSchemaMigratorTests" -v minimal
```

Expected: fail because SQLite infrastructure types do not exist.

- [ ] **Step 3: Implement minimal schema/bootstrap**

Add `Microsoft.Data.Sqlite`, connection factory, schema migration SQL, PBKDF2 password hashing, and startup/bootstrap helpers.

- [ ] **Step 4: Verify green**

Run the same filtered test command and confirm it passes.

### Task 2: SQLite Inventory Store

**Files:**
- Modify: `MarketMafioso.Server/InventoryReportStore.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportStoreSqliteTests.cs`

- [ ] **Step 1: Write failing store tests**

Cover account-scoped save/list/get/latest/delete behavior, character upsert, item-row reconstruction, and account isolation.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~InventoryReportStoreSqliteTests" -v minimal
```

Expected: fail because `InventoryReportStore` still uses JSON files.

- [ ] **Step 3: Implement SQLite store facade**

Keep endpoint-facing methods close to the current names, but route them through account-aware SQLite queries. Reconstruct `StoredInventoryReport` for existing viewer code.

- [ ] **Step 4: Verify green**

Run the same filtered test command and confirm it passes.

### Task 3: Raw JSON Retention

**Files:**
- Modify: `MarketMafioso.Server/InventoryReportStore.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportRawJsonRetentionTests.cs`

- [ ] **Step 1: Write failing retention tests**

Configure `MarketMafioso:RawJsonRetentionCount=2`, save three snapshots, assert only the two newest retain original JSON, and assert the raw JSON endpoint returns `410 Gone` for the pruned snapshot.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~InventoryReportRawJsonRetentionTests" -v minimal
```

Expected: fail because raw JSON retention does not exist.

- [ ] **Step 3: Implement request-body preservation and pruning**

Read inventory POST bodies manually, store original JSON, prune older raw JSON after save, and add explicit raw JSON result states.

- [ ] **Step 4: Verify green**

Run the same filtered test command and confirm it passes.

### Task 4: Dashboard Account Auth and Account Filters

**Files:**
- Create: `MarketMafioso.Server/Auth/DashboardBasicAuthMiddleware.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `.github/workflows/deploy-vps-marketmafioso-dev.yml`
- Test: `MarketMafioso.Server.Tests/DashboardAccountAuthTests.cs`

- [ ] **Step 1: Write failing auth/filter tests**

Cover dashboard `401` without credentials, `200` with bootstrap credentials, ingest key resolving to the default account, and dashboard listing filtered to a selected account/character.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~DashboardAccountAuthTests" -v minimal
```

Expected: fail because app-managed dashboard auth and account filtering do not exist.

- [ ] **Step 3: Implement middleware and filters**

Challenge dashboard routes with Basic Auth from SQLite, preserve public `/health`, keep API-key auth for ingest/read routes, and scope dashboard reads by authenticated account plus optional character filter.

- [ ] **Step 4: Verify green**

Run the same filtered test command and confirm it passes.

### Task 5: Existing JSON Import

**Files:**
- Create: `MarketMafioso.Server/Migration/JsonSnapshotImporter.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/JsonSnapshotImporterTests.cs`

- [ ] **Step 1: Write failing import tests**

Create temp `data/reports/*.json` files, import into the default account, assert idempotency, assert corrupt files are skipped, and assert imported snapshots upsert characters.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~JsonSnapshotImporterTests" -v minimal
```

Expected: fail because importer does not exist.

- [ ] **Step 3: Implement importer**

Import valid `StoredInventoryReport` files into SQLite without deleting source files and without duplicating existing snapshot IDs.

- [ ] **Step 4: Verify green**

Run the same filtered test command and confirm it passes.

### Task 6: Docs and Full Verification

**Files:**
- Modify: `docs/hosted-receiver.md`
- Modify: `docs/local-backend.md`
- Modify: `README.md`

- [ ] **Step 1: Update docs**

Document SQLite location, backup expectations, raw JSON retention, account bootstrap, self-hosted package boundary, and hosted public multi-user deferral.

- [ ] **Step 2: Run full verification**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
dotnet format "MarketMafioso.sln" --verify-no-changes
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
dotnet test "MarketMafioso.sln" -c Debug -v minimal
```

Expected: all commands exit `0`.
