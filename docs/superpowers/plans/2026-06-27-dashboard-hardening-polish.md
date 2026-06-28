# Dashboard Rebuild And Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the Blazor/MudBlazor MarketMafioso dashboard as a reliable operational app for acquisition, inventory, diagnostics, snapshots, and receiver health.

**Architecture:** Keep the server as the source of truth for acquisition requests, attempts, diagnostics, inventory snapshots, auth sessions, and dashboard settings. Keep the Blazor client responsible for presentation state, filters, selected rows, local request-builder queues, and browser-side debugging.

**Tech Stack:** .NET 10 Blazor WebAssembly, MudBlazor 8.14, ASP.NET Core JSON APIs, server-sent events, SQLite-backed server state.

---

## Consolidated Source Of Truth

This plan supersedes the earlier dashboard hardening checklist that was written before the Blazor component split landed. Historical spike/design docs are still useful for context, but this file is the active dashboard rebuild checklist.

Use these docs as supporting references:

- `docs/superpowers/specs/2026-06-27-dashboard-rebuild-design.md`
- `docs/design/2026-06-25-market-acquisition-roadmap.md`
- `docs/design/2026-06-27-market-acquisition-run-model.md`
- `docs/design/2026-06-23-sqlite-inventory-backend.md`
- `docs/design/2026-06-24-inventory-browser-semantics.md`

## Current Status

- [x] Blazor WebAssembly dashboard project exists.
- [x] MudBlazor shell is hosted by `MarketMafioso.Server`.
- [x] Cookie-backed dashboard login/session APIs exist.
- [x] Canonical hosted dashboard route is `/marketmafioso/`.
- [x] Canonical hosted machine/API route namespace is `/marketmafioso/api/*`.
- [x] Old hosted `/api/marketmafioso/*` compatibility is retired.
- [x] Acquisition page is the default dashboard page.
- [x] Acquisition request builder is componentized.
- [x] Acquisition request builder uses account-associated character choices instead of free-typed target character/world.
- [x] Item lookup uses the shared XIV data API.
- [x] Acquisition queue staging, cancel, resend, and request refresh exist.
- [x] SSE updates feed the acquisition request queue.
- [x] Request grid and request details drawer exist.
- [x] Settings has dashboard defaults for character, region, routing, and pickup expiry.
- [x] Persistent diagnostics storage exists.
- [x] Settings has a basic diagnostics grid and event drawer.
- [x] Acquisition page spacing, text hierarchy, and first-pass table polish are complete.
- [x] Inventory page has a first functional Blazor pass against structured inventory APIs.
- [x] Overview page has a first functional summary of receiver health, inventory ingest, recent acquisition activity, and diagnostics.
- [x] Settings Diagnostics has structured category, severity, source, and text filters.
- [x] Settings Diagnostics retention and SSE endpoint visibility exists.
- [x] Settings Snapshots lists recent account-scoped structured snapshots.
- [x] Settings Snapshots exposes raw JSON retention visibility.
- [x] Acquisition attempt timeline and purchase audit rows are visible from the dashboard.
- [ ] Completed and failed acquisition requests are archived and reusable as presets.
- [ ] Remaining server-rendered dashboard HTML is removed after equivalent Blazor pages exist.

## Task 1: Acquisition Page Polish

**Files:**

- Modify: `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify: `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- Modify: `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [x] Remove duplicate refresh controls. Keep one queue refresh surface in or near the request grid.
- [x] Improve text spacing so labels, section headers, helper text, and buttons have enough breathing room at desktop widths.
- [x] Keep the left request-builder panel compact but avoid cramming field labels against panel edges.
- [x] Keep item search stable while typing and ensure lookup cancellation does not clear valid user text or show scary toasts for normal cancellation.
- [x] Keep the queue table dense, readable, and horizontally stable.
- [x] Build `MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj`.

## Task 2: Settings Workspaces

**Files:**

- Modify: `MarketMafioso.Dashboard/Pages/Settings.razor`
- Modify: `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticsEventGrid.razor`
- Modify: `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticEventDrawer.razor`
- Modify: `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`
- Modify server APIs only if missing fields block the UI.

- [x] Keep General defaults useful for acquisition: default character, region, routing, and pickup expiry.
- [x] Add diagnostics filters for category, severity, source, correlation id, request id, and message text.
- [x] Add diagnostics retention visibility.
- [x] Add SSE endpoint and cadence visibility.
- [x] Add snapshot management shell under Settings with latest structured snapshot visibility.
- [x] Add raw JSON retention visibility.
- [x] Build dashboard and run focused server tests if new API fields are added.

## Task 3: Inventory Rebuild

**Files:**

- Modify: `MarketMafioso.Dashboard/Pages/Inventory.razor`
- Create or modify inventory components under `MarketMafioso.Dashboard/Components/Inventory/`
- Modify: `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`
- Modify: `MarketMafioso.Dashboard/Models/DashboardModels.cs`
- Modify server inventory APIs only if the current structured APIs do not expose required data.

- [x] Add account-scoped character selector.
- [x] Show latest snapshot age for the selected character.
- [x] Show inventory scopes for player inventory, retainers, and retainer market listings.
- [x] Keep retainer market listings visually separate from regular inventory entries.
- [x] Use dense sortable/resizable tables matching the Craft Architect-like operational style.
- [ ] Preserve optional icon column support, hidden by default.
- [x] Build dashboard and run focused inventory/server tests if API projections change.

## Task 4: Overview Rebuild

**Files:**

- Modify: `MarketMafioso.Dashboard/Pages/Overview.razor`
- Modify: `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`
- Modify server APIs only if no existing endpoint can support the summary.

- [x] Show receiver health.
- [x] Show latest inventory ingest.
- [x] Show raw/structured retention counts.
- [x] Show active/recent acquisition requests.
- [x] Show diagnostics severity summary.
- [x] Build dashboard.

## Task 5: Acquisition Attempt Timeline And Archive

**Files:**

- Modify: `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
- Modify: `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- Modify: `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`
- Modify server acquisition APIs/stores only if existing request projections do not expose attempt/purchase audit facts.

- [x] Show attempt identity and current/latest attempt state.
- [x] Show route events in chronological order.
- [x] Show purchase attempts, skips, and stop classifications.
- [ ] Preserve completed and failed requests in the request grid by default.
- [ ] Add archive affordance only after completed/failed rows remain inspectable.
- [ ] Add preset/reuse affordance after archive behavior is stable.
- [ ] Build dashboard and run focused acquisition/server tests.

## Task 6: Remove Superseded Server-Rendered Dashboard HTML

**Files:**

- Modify: `MarketMafioso.Server/Program.cs`
- Modify tests under `MarketMafioso.Server.Tests/`

- [ ] Confirm Blazor equivalents exist for acquisition, inventory, overview, settings diagnostics, and settings snapshots.
- [ ] Remove obsolete string-rendered dashboard pages.
- [ ] Keep JSON/plugin/raw report endpoints intact.
- [ ] Add route tests proving dashboard routes serve the Blazor shell and API routes serve JSON.
- [ ] Run focused server tests and dashboard build.

## Verification Rhythm

Use focused checks first:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

When server APIs change:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
```

Before handoff:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

After deployment, smoke the hosted dev route:

```text
https://dev.xivcraftarchitect.com/marketmafioso/
https://dev.xivcraftarchitect.com/marketmafioso/health
https://dev.xivcraftarchitect.com/marketmafioso/api/xivdata/items/search?q=Varn&limit=12
https://dev.xivcraftarchitect.com/api/marketmafioso/
```

Expected: canonical dashboard/API routes work, and the retired `/api/marketmafioso/*` route shape does not serve the dashboard or API.
