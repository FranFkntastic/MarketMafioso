# Inventory Browser Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split gil and retainer market listings from regular inventory items, carry item type through storage, and land the approved dense inventory browser mockup behavior.

**Architecture:** Add backwards-compatible optional payload fields, store retainer gil on inventory owners, store market listings in their own table, and build dashboard views from structured SQLite data. The browser renders normal inventory as a dense table and keeps market listings in a separate individual-listing section.

**Tech Stack:** C# 12, Dalamud plugin models/scanner/cache, ASP.NET minimal APIs, Microsoft.Data.Sqlite, xUnit, inline HTML/CSS/JS dashboard rendering.

---

### Task 1: Contracts

**Files:**
- Modify: `MarketMafioso/InventoryPayload.cs`
- Modify: `MarketMafioso/Configuration.cs`
- Modify: `MarketMafioso.Server/Models.cs`

- [ ] Add optional `ItemType` to item slots and cached retainer items.
- [ ] Add optional/defaulted retainer `Gil` and `MarketListings`.
- [ ] Keep deserialization compatible with old plugin config and old JSON payloads.

### Task 2: Capture and Reporting

**Files:**
- Modify: `MarketMafioso/InventoryScanner.cs`
- Modify: `MarketMafioso/RetainerCacheManager.cs`
- Modify: `MarketMafioso/HttpReporter.cs`

- [ ] Split normal retainer pages from `RetainerGil` and `RetainerMarket`.
- [ ] Put gil into retainer owner state, not an item bag.
- [ ] Put market listings into a separate retainer listing collection.
- [ ] Copy gil, market listings, and item type through cached retainers into outbound reports.

### Task 3: SQLite Persistence

**Files:**
- Modify: `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs`
- Modify: `MarketMafioso.Server/InventoryReportStore.cs`
- Test: `MarketMafioso.Server.Tests/SqliteSchemaMigratorTests.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportStoreSqliteTests.cs`

- [ ] Add idempotent migrations for `inventory_owners.gil`, `inventory_items.item_type`, and `retainer_market_listings`.
- [ ] Persist and reload gil, item type, and market listings.
- [ ] Exclude gil and market listings from item summary counts.

### Task 4: Browser View Model

**Files:**
- Modify: `MarketMafioso.Server/InventoryBrowserModels.cs`
- Modify: `MarketMafioso.Server/InventoryBrowserViewBuilder.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`

- [ ] Add scope view models with gil, listing counts, and data age.
- [ ] Add market-listing view models with per-listing price fields.
- [ ] Add item type to aggregate item rows.
- [ ] Filter regular inventory by optional retainer scope.

### Task 5: Dashboard UI

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Reference: `mockups/inventory-browser-target.html`

- [ ] Render retainer scopes with gil, listing count, and data age.
- [ ] Render individual market listings separately from normal inventory.
- [ ] Render proportional columns, filter buttons beside labels, clearer separators, and body/header resize handles.
- [ ] Make search update automatically as the user types.
- [ ] Keep the icon column hidden by default.

### Task 6: Verification

**Files:**
- All touched files.

- [ ] Run `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal`.
- [ ] Run `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal`.
- [ ] Run `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- [ ] For plugin capture changes, run `MarketMafioso/tools/Deploy-DevPlugin.ps1` when ready for in-game validation.
