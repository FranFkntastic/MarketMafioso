# Inventory JSON Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a parsed inventory snapshot viewer to the local server panel, backed by a reusable server-side view model, endpoint, and versioned snapshot metadata.

**Architecture:** Keep stored reports as-is, build a display-oriented `InventorySnapshotView` from `StoredInventoryReport`, expose that model at `/api/reports/{id}/view`, and render `/reports/{id}` from the same model. Add a small server test project so parsing behavior is covered independently of Dalamud.

**Tech Stack:** C# 12, ASP.NET Core minimal APIs, .NET 10, xUnit-style server tests via `Microsoft.NET.Test.Sdk`.

---

### Task 1: Server Test Project

**Files:**
- Create: `MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj`
- Create: `MarketMafioso.Server.Tests/InventorySnapshotViewBuilderTests.cs`
- Modify: `MarketMafioso.sln`

- [ ] **Step 1: Add a test project referencing `MarketMafioso.Server`**

Create a net10.0 test project with package references to `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `coverlet.collector`.

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln "MarketMafioso.sln" add "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj"`

- [ ] **Step 3: Write failing builder tests**

Test a stored report containing one player bag and one retainer bag. Assert that the builder returns one player section, one retainer section, expected item labels, and totals computed from rows. Also test an empty report produces explicit empty sections.

- [ ] **Step 4: Run tests to verify red**

Run: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -v minimal`

Expected: compile failure because `InventorySnapshotViewBuilder` does not exist.

### Task 2: Viewer Model and Builder

**Files:**
- Create: `MarketMafioso.Server/InventorySnapshotView.cs`
- Create: `MarketMafioso.Server/InventorySnapshotViewBuilder.cs`
- Test: `MarketMafioso.Server.Tests/InventorySnapshotViewBuilderTests.cs`

- [ ] **Step 1: Add viewer records**

Create records for `InventorySnapshotView`, `InventoryOwnerView`, `InventoryBagView`, `InventoryItemView`, and `InventorySnapshotTotals`.

- [ ] **Step 2: Add `InventorySnapshotViewBuilder.Build(StoredInventoryReport stored)`**

Map player inventory into one owner named `Player Inventory`, map retainers by retainer name/id/lastUpdated, map bags and items directly, compute totals from item rows, and preserve empty owner lists.

- [ ] **Step 3: Run builder tests to verify green**

Run: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -v minimal`

Expected: tests pass.

### Task 3: Parsed View API

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Create: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`

- [ ] **Step 1: Make server app testable**

Expose the generated `Program` type to tests with `public partial class Program`.

- [ ] **Step 2: Write failing endpoint tests**

Use `WebApplicationFactory<Program>` to post the sample inventory report, follow the created id, call `/api/reports/{id}/view`, and assert the parsed response contains player and retainer owners. Assert a missing id returns 404.

- [ ] **Step 3: Implement `/api/reports/{id}/view`**

Load the stored report. Return 404 when missing. Return `InventorySnapshotViewBuilder.Build(report)` when present.

- [ ] **Step 4: Run endpoint tests to verify green**

Run: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -v minimal`

Expected: tests pass.

### Task 4: HTML Detail Viewer

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`

- [ ] **Step 1: Render detail page from the parsed view**

Update `/reports/{id}` to build an `InventorySnapshotView` and pass it into `RenderReportDetails`.

- [ ] **Step 2: Replace raw-only detail body with grouped tables**

Render summary cards, a player inventory section, retainer sections grouped by retainer and bag, empty states, a raw JSON link, and a lower raw JSON block.

- [ ] **Step 3: Keep raw JSON endpoint unchanged**

Do not alter `/api/reports/{id}`.

### Task 5: Verification

**Files:**
- All modified files

- [ ] **Step 1: Run focused tests**

Run: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -v minimal`

- [ ] **Step 2: Run solution tests**

Run: `dotnet test "MarketMafioso.sln" -v minimal`

- [ ] **Step 3: Run Debug build**

Run: `dotnet build "MarketMafioso.sln" -c Debug`

- [ ] **Step 4: Run format check**

Run: `dotnet format "MarketMafioso.sln" --verify-no-changes`

- [ ] **Step 5: Inspect git status**

Run: `git status --short --branch`

### Task 6: Snapshot Metadata

**Files:**
- Modify: `MarketMafioso/InventoryPayload.cs`
- Modify: `MarketMafioso/HttpReporter.cs`
- Modify: `MarketMafioso.Server/Models.cs`
- Modify: `MarketMafioso.Server/InventorySnapshotView.cs`
- Modify: `MarketMafioso.Server/InventorySnapshotViewBuilder.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `docs/samples/inventory-report.sample.json`
- Modify: `docs/local-backend.md`

- [ ] **Step 1: Add metadata contract tests**

Extend builder and endpoint tests so reports with metadata expose schema version, source plugin, plugin version, and generated timestamp in the parsed view. Also assert old reports without metadata display unknown metadata values.

- [ ] **Step 2: Add server metadata models**

Add nullable `InventoryReport.Metadata` for raw reports and non-null parsed metadata on `InventorySnapshotView`.

- [ ] **Step 3: Emit plugin metadata**

Set `schemaVersion` to `1`, `sourcePlugin` to `MarketMafioso`, `pluginVersion` from the plugin assembly version, and `generatedAtUtc` from the same UTC timestamp used by the legacy `timestamp` field.

- [ ] **Step 4: Show metadata in the detail page**

Display schema, source, plugin version, and generated timestamp in the snapshot header.

### Task 7: Hosted Receiver Readiness

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `docs/local-backend.md`

- [ ] **Step 1: Add hosted auth and base-path tests**

Use `WebApplicationFactory<Program>` with in-memory configuration for `MarketMafioso:ApiKey`, `MarketMafioso:RequireApiKey`, and `MarketMafioso:BasePath`. Assert unauthenticated API/ingest requests are rejected, authenticated requests work, and `/api/marketmafioso/inventory` routes through the base path.

- [ ] **Step 2: Add server hosted options**

Read `RequireApiKey`, `ApiKey`, and `BasePath` from configuration. Apply `UsePathBase` when configured. Require `X-Api-Key` on inventory ingestion and `/api/reports` routes when hosted auth is enabled. Keep `/health` public and leave dashboard HTML to Caddy Basic Auth.

- [ ] **Step 3: Add plugin endpoint presets**

Add buttons in the settings window for local, dev VPS, and production VPS endpoints. Preserve manual URL editing and do not silently rewrite existing configs.

- [ ] **Step 4: Document hosted receiver workflow**

Document dev/prod URLs, auth requirements, environment-local storage, and local receiver fallback.
