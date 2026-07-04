# Client Quick Shop Dashboard Routes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development only when tasks are mostly independent; use superpowers:executing-plans for tightly coupled work. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a lightweight client-native quick-shop constructor to the Market Acquisition tab that creates a server-backed, dashboard-visible acquisition batch, immediately claims and accepts it for the local plugin instance, and then reuses the existing plan preparation, route execution, progress reporting, purchase audit, reprepare, and world-visit catalog behavior.

**Architecture:** Treat the client panel as an alternate request authoring surface. The server remains the source of truth for acquisition batches. The plugin builds a `MarketAcquisitionBatchCreateRequest` with `Origin = "ClientQuickShop"` and `CreatedByPluginInstanceId`, posts it to `/api/acquisition/batches`, claims the created batch, accepts it locally, persists the accepted claim, and leaves preparation/start under the current claimed-batch controls. Dashboard-created batches continue to omit origin or use `DashboardCreated`.

**Tech Stack:** C#/.NET, Dalamud ImGui plugin UI, ASP.NET Core minimal API, SQLite-backed server store with payload JSON request bodies, Blazor/MudBlazor dashboard, xUnit tests.

---

## File Structure

Server/API:

- `src/MarketMafioso.Server/MarketAcquisitionModels.cs`
- `src/MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- `tests/MarketMafioso.Server.Tests/MarketAcquisitionRequestStoreTests.cs`
- `tests/MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

Dashboard:

- `src/MarketMafioso.Dashboard/Models/DashboardModels.cs`
- `src/MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- `src/MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
- `src/MarketMafioso.Dashboard/Pages/Overview.razor`

Plugin:

- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraft.cs`
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraftValidator.cs`
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopRequestBuilder.cs`
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopWorkflow.cs`
- `src/MarketMafioso/Windows/MainWindow.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopDraftValidatorTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopRequestBuilderTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopWorkflowTests.cs`

---

## Origin Contract

Add shared origin constants independently in server, dashboard, and plugin model files to avoid a new shared assembly:

```csharp
public static class MarketAcquisitionOrigins
{
    public const string DashboardCreated = "DashboardCreated";
    public const string ClientQuickShop = "ClientQuickShop";
}
```

Add these fields to both single-request and batch-create DTOs, plus the server/dashboard/plugin view DTOs:

```csharp
public string Origin { get; init; } = MarketAcquisitionOrigins.DashboardCreated;
public string? CreatedByPluginInstanceId { get; init; }
```

Rules:

- Missing or blank `Origin` is normalized to `DashboardCreated`.
- Only `DashboardCreated` and `ClientQuickShop` are accepted.
- `CreatedByPluginInstanceId` is optional metadata, but the plugin sends it for quick-shop batches.
- Origin does not affect claim, accept, planning, purchase reporting, cancellation, resend, or dashboard template behavior.
- No SQLite column migration is required because acquisition request bodies are already persisted as `payload_json` and converted through `ReadPrimaryCreateRequest`.

---

## Tasks

- [ ] Add origin metadata to the server DTO and store projection.

  Files:

  - `src/MarketMafioso.Server/MarketAcquisitionModels.cs`
  - `src/MarketMafioso.Server/MarketAcquisitionRequestStore.cs`

  Implementation:

  - Add `MarketAcquisitionOrigins`.
  - Add `Origin` and `CreatedByPluginInstanceId` to `MarketAcquisitionCreateRequest`, `MarketAcquisitionBatchCreateRequest`, `MarketAcquisitionRequestView`, and `MarketAcquisitionClaimView`.
  - In `ValidateCreateRequest` and `ValidateBatchCreateRequest`, reject unsupported nonblank origins.
  - Add a helper:

    ```csharp
    private static string NormalizeOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin)
            ? MarketAcquisitionOrigins.DashboardCreated
            : origin.Trim();
    ```

  - In `ToBatchCreateRequest`, copy `Origin` and `CreatedByPluginInstanceId`.
  - In `ToPrimaryCreateRequest`, copy `Origin` and `CreatedByPluginInstanceId`.
  - In `ToView`, set normalized `Origin` and `CreatedByPluginInstanceId`.
  - In `ToClaimView`, copy `Origin` and `CreatedByPluginInstanceId`.
  - Confirm old stored payloads deserialize and display as `DashboardCreated`.

  Tests:

  - Store test: creating a batch with `Origin = ClientQuickShop` returns view origin and created plugin instance ID.
  - Store test: creating a batch with omitted origin returns `DashboardCreated`.
  - Store test: invalid origin throws `ArgumentException`.
  - Endpoint test: `POST /api/acquisition/batches` echoes quick-shop origin in the created response.

- [ ] Add origin metadata to dashboard models and display.

  Files:

  - `src/MarketMafioso.Dashboard/Models/DashboardModels.cs`
  - `src/MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
  - `src/MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
  - `src/MarketMafioso.Dashboard/Pages/Overview.razor`

  Implementation:

  - Add `MarketAcquisitionOrigins` constants and the two view fields to dashboard `MarketAcquisitionRequestView`.
  - In `ServerRequestGrid.razor`, add a compact origin chip beside status or under the item target:

    ```csharp
    private static string OriginLabel(MarketAcquisitionRequestView request) =>
        request.Origin.Equals(MarketAcquisitionOrigins.ClientQuickShop, StringComparison.OrdinalIgnoreCase)
            ? "Quick Shop"
            : "Dashboard";
    ```

  - In `RequestDetailsDrawer.razor`, show `Origin` as `Quick Shop` or `Dashboard`; show `CreatedByPluginInstanceId` only when present.
  - In `Overview.razor`, preserve the current compact table and add the same origin label only if it fits without making the overview noisy; otherwise skip overview origin display.

  Tests:

  - No automated component test is required unless the project already has bUnit coverage. Verify with build and dashboard smoke inspection.

- [ ] Add plugin create request DTOs and client create method.

  Files:

  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`

  Implementation:

  - Add plugin-local `MarketAcquisitionOrigins`.
  - Add `Origin` and `CreatedByPluginInstanceId` to plugin `MarketAcquisitionRequestView` and `MarketAcquisitionClaimView` via inheritance.
  - Add plugin request DTOs:

    ```csharp
    public sealed record MarketAcquisitionBatchCreateRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;
        [JsonPropertyName("idempotencyKey")]
        public string IdempotencyKey { get; init; } = string.Empty;
        [JsonPropertyName("origin")]
        public string Origin { get; init; } = MarketAcquisitionOrigins.ClientQuickShop;
        [JsonPropertyName("createdByPluginInstanceId")]
        public string? CreatedByPluginInstanceId { get; init; }
        public string TargetCharacterName { get; init; } = string.Empty;
        public string TargetWorld { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public string WorldMode { get; init; } = string.Empty;
        public string SweepScope { get; init; } = "Region";
        public List<string> SweepDataCenters { get; init; } = new();
        public int ExpiresInSeconds { get; init; } = 300;
        public List<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = new();
    }
    ```

  - Add `MarketAcquisitionBatchLineCreateRequest` matching server/dashboard line create shape.
  - Add `CreateBatchAsync(serverUrl, clientApiKey, request, token)` to `MarketAcquisitionRequestClient`, posting to `{ResolveAcquisitionBaseUrl(serverUrl)}/batches` with `X-Api-Key`.
  - Deserialize and return `MarketAcquisitionRequestView`.
  - Preserve `EnsureSuccessStatusCode` behavior for create failures.

  Tests:

  - `CreateBatchAsync_PostsBatchPayloadAndReturnsServerView`
  - Assert URL resolves from hosted inventory API to hosted acquisition batch endpoint.
  - Assert `X-Api-Key`, `origin`, `createdByPluginInstanceId`, idempotency key, shared routing fields, and multiple lines are serialized.

- [ ] Build quick-shop draft, validator, and request builder.

  Files:

  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraft.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraftValidator.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopRequestBuilder.cs`
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopDraftValidatorTests.cs`
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopRequestBuilderTests.cs`

  Draft model:

  ```csharp
  public sealed record MarketAcquisitionQuickShopDraft
  {
      public string DraftId { get; init; } = Guid.NewGuid().ToString("N");
      public int DraftRevision { get; init; } = 1;
      public string Region { get; init; } = "North America";
      public string WorldMode { get; init; } = "Recommended";
      public string SweepScope { get; init; } = "Region";
      public List<string> SweepDataCenters { get; init; } = new();
      public List<MarketAcquisitionQuickShopLineDraft> Lines { get; init; } = new();
  }
  ```

  Line model:

  ```csharp
  public sealed record MarketAcquisitionQuickShopLineDraft
  {
      public uint ItemId { get; init; }
      public string ItemName { get; init; } = string.Empty;
      public string QuantityMode { get; init; } = "AllBelowThreshold";
      public uint TargetQuantity { get; init; }
      public uint MaxQuantity { get; init; }
      public string HqPolicy { get; init; } = "Either";
      public uint MaxUnitPrice { get; init; }
      public uint GilCap { get; init; }
  }
  ```

  Validator behavior:

  - Current character name and world must be available.
  - API key must be present.
  - At least one line is required.
  - Every line must have nonzero `ItemId` and `MaxUnitPrice`.
  - `TargetQuantity` lines require `TargetQuantity > 0`.
  - `AllBelowThreshold` lines use optional `MaxQuantity`; zero means no explicit quantity cap.
  - HQ policy must be one of `Either`, `HQOnly`, `NQOnly`.
  - World mode must be `Recommended` or `AllWorldSweep`.
  - `AllWorldSweep` with `SweepScope = DataCenters` requires at least one selected data center.

  Builder behavior:

  - Stable idempotency key format:

    ```csharp
    $"{pluginInstanceId}:quick-shop:{draft.DraftId}:{draft.DraftRevision}"
    ```

  - Set `Origin = ClientQuickShop`.
  - Set `CreatedByPluginInstanceId = pluginInstanceId`.
  - Map each draft line into a `MarketAcquisitionBatchLineCreateRequest`.
  - Preserve route settings at batch level.
  - Set `ExpiresInSeconds = 300`.

  Tests:

  - Valid multi-item draft produces a batch create request with two lines.
  - Same draft revision produces the same idempotency key.
  - Incremented draft revision produces a different idempotency key.
  - Validation returns all relevant errors at once for an empty/bad draft.
  - Data-center sweep rejects empty data-center selection.

- [ ] Add quick-shop workflow service for create, claim, accept.

  Files:

  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopWorkflow.cs`
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopWorkflowTests.cs`

  Implementation:

  - Keep orchestration outside `MainWindow` so UI drawing remains thin.
  - Workflow inputs:

    ```csharp
    public sealed record MarketAcquisitionQuickShopWorkflowRequest(
        string ServerUrl,
        string ClientApiKey,
        string CharacterName,
        string World,
        string PluginInstanceId,
        MarketAcquisitionQuickShopDraft Draft);
    ```

  - Workflow output:

    ```csharp
    public sealed record MarketAcquisitionQuickShopWorkflowResult(
        MarketAcquisitionRequestView Created,
        MarketAcquisitionClaimView Claimed,
        MarketAcquisitionRequestView Accepted,
        string AcceptIdempotencyKey);
    ```

  - Execute:

    1. Validate draft.
    2. Build create request.
    3. `CreateBatchAsync`.
    4. `ClaimAsync` using the returned request id.
    5. `AcceptAsync` using idempotency key:

       ```csharp
       $"{pluginInstanceId}:quick-shop:{draft.DraftId}:{draft.DraftRevision}:accept"
       ```

    6. Return the accepted state and accept key for claim persistence.

  - If create replays due idempotency, claim/accept should still run against the returned request id; existing server behavior will decide whether the claim is still valid.
  - A server conflict or auth failure should surface as the existing HTTP exception; the UI will display `ex.Message`.

  Testing approach:

  - If `MarketAcquisitionRequestClient` remains concrete, introduce a small `IMarketAcquisitionRequestClient` interface implemented by the current client and fake it in workflow tests.
  - Test happy path call order: create -> claim -> accept.
  - Test validation failure avoids network calls.
  - Test accept idempotency key is stable.

- [ ] Add the plugin quick-shop panel to `MainWindow`.

  File:

  - `src/MarketMafioso/Windows/MainWindow.cs`

  Implementation:

  - Add fields near existing acquisition state:

    ```csharp
    private MarketAcquisitionQuickShopDraft quickShopDraft = MarketAcquisitionQuickShopDraft.CreateDefault();
    private string quickShopItemIdBuffer = string.Empty;
    private string quickShopItemNameBuffer = string.Empty;
    private string quickShopTargetQuantityBuffer = string.Empty;
    private string quickShopMaxQuantityBuffer = string.Empty;
    private string quickShopMaxUnitPriceBuffer = string.Empty;
    private string quickShopGilCapBuffer = string.Empty;
    ```

  - Draw `DrawMarketAcquisitionQuickShopSection()` between the module summary and request pickup, before claimed batch display.
  - Hide or disable the panel while a guided route is active, matching request pickup behavior.
  - Controls:

    - Region text/select using existing North America default.
    - World mode selector with `Recommended` and `AllWorldSweep`.
    - Sweep scope/data-center controls only when all-world sweep is selected.
    - Line editor fields: item ID, optional name, quantity mode, target/max quantity, HQ policy, max unit price, gil cap.
    - Buttons: Add Line, Duplicate Line, Remove Line, Clear Draft.
    - Primary action: `Create Monitored Route`.

  - Button enablement:

    - disabled when `acquisitionRequestBusy`
    - disabled without API key
    - disabled without current character scope
    - disabled when validator returns errors

  - `CreateMonitoredQuickShopRouteAsync`:

    1. set `acquisitionRequestBusy = true`
    2. call workflow
    3. set `claimedAcquisitionRequest = result.Claimed with { Status = result.Accepted.Status }`
    4. persist with `MarketAcquisitionClaimPersistence.Save(config, claimedAcquisitionRequest, result.AcceptIdempotencyKey, rejectIdempotencyKey: null)`
    5. clear `pendingAcquisitionRequests` or leave them unchanged; do not auto-fetch
    6. reset `acquisitionPlan`, `acquisitionRouteRunner`, and route run summaries if the previous active claim differs
    7. increment/replace draft with a new `DraftId`
    8. show a status message that the monitored route is ready for `Prepare Plan`

  - Do not call `PrepareMarketAcquisitionPlanAsync` automatically in the first implementation slice.
  - Do not auto-start the guided route.

  Manual verification:

  - In-game plugin tab shows quick-shop panel.
  - Creating a one-line route produces a claimed accepted batch.
  - Creating a multi-line route shows all lines in the claimed batch table.
  - `Prepare Plan` works immediately after creation.

- [ ] Add dashboard smoke display for quick-shop origin.

  Files:

  - `src/MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
  - `src/MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`

  Manual verification:

  - Quick-shop route appears on the dashboard request board without a refresh if SSE is connected.
  - Request board shows `Quick Shop` origin.
  - Drawer shows origin and plugin instance ID.
  - Timeline, line progress, and purchase audit continue to populate after route execution.

- [ ] Run focused and broad verification.

  Commands:

  ```powershell
  dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~MarketAcquisition"
  dotnet test .\tests\MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj --filter "FullyQualifiedName~MarketAcquisition"
  dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj
  dotnet test .\tests\MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj
  dotnet build .\src\MarketMafioso.Dashboard\MarketMafioso.Dashboard.csproj
  ```

  Expected results:

  - Market Acquisition plugin tests pass.
  - Market Acquisition server tests pass.
  - Full plugin test project remains green.
  - Full server test project remains green.
  - Dashboard project builds.

- [ ] Deploy dev plugin for live client validation when implementation is complete.

  Command:

  ```powershell
  .\src\MarketMafioso\tools\Deploy-DevPlugin.ps1
  ```

  Expected results:

  - Script succeeds.
  - Dev plugin DLL is copied into the XIVLauncher dev plugin path.
  - User can live-test the panel in-game.

---

## Implementation Order

1. Server origin metadata and tests.
2. Dashboard DTO/display metadata.
3. Plugin create DTO/client method and tests.
4. Quick-shop draft/validator/builder and tests.
5. Quick-shop workflow and tests.
6. `MainWindow` ImGui panel integration.
7. Focused tests.
8. Broad tests and dashboard build.
9. Dev plugin deploy for live validation.

---

## Edge Cases

- Existing old requests without origin must display as `Dashboard`.
- Dashboard-created requests must keep current behavior and appear claimable.
- Quick-shop batch create succeeds but claim fails: surface the server error and leave dashboard request visible for normal pickup/resend.
- Quick-shop create replays due idempotency: reuse the returned request id and attempt claim/accept.
- User changes draft after a failure: increment draft revision or create a new draft id before retrying if the request body changes.
- Current scope disappears during route travel: disable quick-shop create and show the same temporary scope-gap language used by pickup.
- All-world sweep recent-world policy remains a planning concern; the quick-shop create request only records route mode/scope.

---

## Recommendation

Use inline execution with `superpowers:executing-plans` for implementation. This feature is coupled across server DTO projection, plugin HTTP models, claim persistence, and `MainWindow` route state, and the current working tree already contains adjacent uncommitted Market Acquisition work. Subagent-driven execution would create more merge and coordination overhead than it saves for this slice.
