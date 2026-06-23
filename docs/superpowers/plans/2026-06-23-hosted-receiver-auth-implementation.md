# Hosted Receiver Auth Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the reviewed hosted receiver auth design for the dev VPS receiver.

**Architecture:** The ASP.NET receiver owns API-key validation for ingest and optional read API routes. Caddy owns browser dashboard Basic Auth. The plugin classifies endpoints from the current URL and blocks hosted sends without an ingest key.

**Tech Stack:** C# 12, ASP.NET Core minimal APIs on .NET 10, xUnit with `WebApplicationFactory`, Dalamud ImGui plugin UI, GitHub Actions, Caddy.

---

### Task 1: Server Auth Core and API Route Policy

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`

- [ ] Add failing tests for ingest/read key separation, previous ingest key acceptance, read API fail-closed behavior, and non-secret JSON `401`.
- [ ] Run `dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" --filter "FullyQualifiedName~InventoryReportViewEndpointTests" -v minimal` and verify the new tests fail for missing behavior.
- [ ] Implement centralized fixed-time API key validation using `CryptographicOperations.FixedTimeEquals`.
- [ ] Rename hosted config from `MarketMafioso:ApiKey` to `MarketMafioso:IngestApiKey`, while accepting the old key only as a compatibility alias when the new key is absent.
- [ ] Add optional `MarketMafioso:PreviousIngestApiKey`, `MarketMafioso:ReadApiKey`, and `MarketMafioso:PreviousReadApiKey`.
- [ ] Make `/api/reports...` accept only read keys when configured and fail closed when no read key is configured.
- [ ] Remove or disable hosted API-key `DELETE /api/reports...` routes.
- [ ] Run the focused server tests and verify they pass.

### Task 2: Dashboard Routes, CSRF, and Hosted Display Safety

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`

- [ ] Add failing tests for `/reports/{id}/json`, `/reports/latest/json`, dashboard links avoiding `/api/reports...`, hosted `Location` including `PathBase`, CSRF rejection/success, and hosted dashboard hiding filesystem paths.
- [ ] Run the focused server tests and verify the new tests fail for missing behavior.
- [ ] Add browser-safe JSON export routes.
- [ ] Update dashboard links to use browser-safe routes.
- [ ] Add CSRF nonce generation and validation for dashboard delete forms.
- [ ] Add `MarketMafioso:PublicOrigin` and validate dashboard POST `Origin` or `Referer` when configured.
- [ ] Replace hosted storage path display with an environment label.
- [ ] Include `PathBase` in hosted `201 Created` locations.
- [ ] Run the focused server tests and verify they pass.

### Task 3: Plugin Hosted Endpoint Guardrails

**Files:**
- Modify: `MarketMafioso/HttpReporter.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] Add endpoint classification helpers for local, known hosted, custom remote, and invalid URLs.
- [ ] Block hosted/custom-remote sends before HTTP when the API key is empty, preserving local receiver behavior.
- [ ] Map hosted `401` responses to an API-key-specific chat/status message.
- [ ] Parse hosted `201 Created` responses and display the direct dashboard report URL.
- [ ] Update the settings UI label/warning so hosted URLs show the API key as required.
- [ ] Disable or clearly label the Production VPS preset as unavailable until production deploy exists.
- [ ] Run `dotnet build "MarketMafioso.sln" -c Debug` and verify the plugin builds and syncs.

### Task 4: Dev Workflow, Docs, and Live Smoke

**Files:**
- Modify: `.github/workflows/deploy-vps-marketmafioso-dev.yml`
- Modify: `docs/hosted-receiver.md`
- Optional helper: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`

- [ ] Update workflow secrets to use `MARKETMAFIOSO_DEV_INGEST_API_KEY`, optional previous/read keys, and `MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD`.
- [ ] Generate the Caddy Basic Auth hash on the runner with fixed username `marketmafioso`.
- [ ] Keep plaintext dashboard password on the runner only.
- [ ] Upload server env/Caddy files through a `mktemp -d` directory with cleanup trap.
- [ ] Install `MarketMafioso__PublicOrigin=https://dev.xivcraftarchitect.com`.
- [ ] Repair Caddy import ordering before the SPA fallback on every deploy.
- [ ] Add smoke checks for public health, unauthenticated dashboard `401`, authenticated dashboard HTML, unauthenticated ingest `401`, authenticated ingest `201`, and optional read API behavior.
- [ ] Update hosted receiver docs with first-time setup, production-unavailable wording, dashboard URL, credential generation, and rotation.
- [ ] Run `dotnet test "MarketMafioso.sln" -v minimal`, `dotnet build "MarketMafioso.sln" -c Debug`, and `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- [ ] Commit, push `local-dev`, watch the GitHub Actions deploy, and run live endpoint smoke tests.
