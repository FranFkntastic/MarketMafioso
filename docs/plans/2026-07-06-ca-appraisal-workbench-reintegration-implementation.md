# CA Appraisal Workbench Reintegration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Craft Architect craft-cost appraisal into Acquisition Workbench, move quote diagnostics into Diagnostics, and remove the standalone CA Companion tab/window.

**Architecture:** Keep the existing `MarketMafioso.CraftArchitectCompanion` service layer. Extract the reusable state/control logic out of `CraftArchitectCompanionWindow`, mount it in `AcquisitionWorkbenchWindow.DrawAppraisePane`, and expose a read-only diagnostics snapshot to `MarketAcquisitionDiagnosticsWindow`.

**Tech Stack:** C#/.NET, Dalamud ImGui windows, xUnit tests, existing Market Acquisition draft/stock/cache services, existing Craft Architect quote providers.

---

## Source Map

- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
  - Add selected-line craft appraisal state, quote controls, threshold apply action, and quote diagnostics handoff.
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
  - Remove `CA Companion` tab and module advertising.
  - Construct workbench with craft appraisal dependencies.
  - Keep provider settings in Settings.
- Modify: `src/MarketMafioso/Plugin.cs`
  - Stop registering `mainWindow.CraftArchitectCompanion`.
- Modify or delete: `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`
  - Extract logic first, then remove the window when callers are gone.
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchState.cs`
  - Holds selected-line quote state, capability state, statuses, and diagnostic paths.
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchController.cs`
  - Owns async quote fetch, capability refresh, quote clear, quote apply, and diagnostic snapshot creation.
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalDiagnosticsSnapshot.cs`
  - Read-only diagnostics model for the Diagnostics window.
- Create or modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalPanelRenderer.cs`
  - Small ImGui renderer for the workbench Appraise pane quote block.
- Modify: `src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
  - Add quote-provider diagnostics section.
- Keep: `src/MarketMafioso/CraftArchitectCompanion/*`
  - Providers, formatter, market appraisal service, and diagnostic printouts remain service code.
- Modify tests under `tests/MarketMafioso.Tests/CraftArchitectCompanion/*`
  - Retarget window-specific tests to presenter/controller behavior.
- Create tests under `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/*`
  - Cover selected-line quote behavior and diagnostics snapshot behavior.

## Task 1: Extract Quote State And Diagnostics Model

**Files:**

- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchState.cs`
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalDiagnosticsSnapshot.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchStateTests.cs`

- [ ] **Step 1: Write failing state tests**

Add tests proving line identity changes invalidate quote evidence while threshold changes do not.

```csharp
[Fact]
public void UpdateSelection_ItemChangeClearsQuoteEvidence()
{
    var state = CraftAppraisalWorkbenchState.CreateForTests();
    state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

    state.UpdateSelectedLine(new CraftAppraisalLineIdentity(9999, "Cobalt Ingot", 1, "Either", "North America"));

    Assert.Null(state.LatestQuote);
    Assert.Null(state.LastCraftQuoteDiagnosticFilePath);
}

[Fact]
public void UpdateThreshold_DoesNotClearQuoteEvidence()
{
    var state = CraftAppraisalWorkbenchState.CreateForTests();
    var identity = new CraftAppraisalLineIdentity(5060, "Darksteel Ingot", 1, "Either", "North America");
    state.UpdateSelectedLine(identity);
    state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

    state.RecordThresholdChanged(1500);

    Assert.NotNull(state.LatestQuote);
    Assert.Equal("quote.log", state.LastCraftQuoteDiagnosticFilePath);
}
```

- [ ] **Step 2: Run the failing tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbenchStateTests" --no-restore
```

Expected: fails because the new state model does not exist.

- [ ] **Step 3: Add the state and snapshot models**

Implement immutable line identity plus mutable workbench state:

```csharp
public sealed record CraftAppraisalLineIdentity(
    uint ItemId,
    string ItemName,
    uint Quantity,
    string HqPolicy,
    string Region);

public sealed record CraftAppraisalDiagnosticsSnapshot
{
    public bool WorkshopHostEnabled { get; init; }
    public bool WorkshopHostAvailable { get; init; }
    public DateTimeOffset? CapabilitiesCheckedAtUtc { get; init; }
    public string WorkshopHostStatus { get; init; } = "Workshop Host quote API not checked.";
    public string CraftQuoteStatus { get; init; } = "No craft quote yet.";
    public string? LastCraftQuoteDiagnosticFilePath { get; init; }
    public string? LastMarketDepthDiagnosticFilePath { get; init; }
    public string? LastQuoteItemName { get; init; }
    public uint LastQuoteItemId { get; init; }
    public bool LatestQuoteWasLastGood { get; init; }
}
```

- [ ] **Step 4: Verify tests pass**

Run the same focused test command. Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchState.cs src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalDiagnosticsSnapshot.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchStateTests.cs
git commit -m "refactor: add workbench craft appraisal state"
```

## Task 2: Extract Quote Controller From The Standalone Window

**Files:**

- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchController.cs`
- Modify: `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchControllerTests.cs`

- [ ] **Step 1: Write failing controller tests**

Cover capability refresh, quote fetch success, quote fetch failure, and explicit threshold apply.

```csharp
[Fact]
public async Task FetchQuoteAsync_RecordsQuoteAndDiagnosticPath()
{
    var provider = new StubQuoteProvider(TestQuote("Darksteel Ingot", 5060, 1200m));
    var controller = CraftAppraisalWorkbenchController.CreateForTests(provider);

    await controller.FetchQuoteAsync(TestRequest(5060, "Darksteel Ingot"));

    Assert.Equal(1200m, controller.State.LatestQuote?.EstimatedUnitCost);
    Assert.Contains("refreshed", controller.State.CraftQuoteStatus, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ApplyQuoteToThreshold_ReturnsRoundedGilOnlyWhenQuoteIsComplete()
{
    var controller = CraftAppraisalWorkbenchController.CreateForTests(
        new StubQuoteProvider(TestQuote("Darksteel Ingot", 5060, 1200.49m)));
    controller.State.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200.49m), "quote.log");

    var threshold = controller.TryGetQuoteThreshold();

    Assert.Equal(1200u, threshold);
}
```

- [ ] **Step 2: Run failing tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbenchControllerTests" --no-restore
```

Expected: fails because the controller does not exist.

- [ ] **Step 3: Move async quote behavior into the controller**

Lift these responsibilities out of `CraftArchitectCompanionWindow`:

- `EnsureWorkshopHostCapabilitiesFreshAsync`
- `RefreshWorkshopHostCapabilitiesAsync`
- `RefreshCraftQuoteAsync`
- `GetCraftQuoteAsync`
- quote diagnostic printout writing
- quote result logging

The controller constructor should accept:

- `Configuration`
- `ICraftQuoteProvider`
- `WorkshopHostCapabilitiesClient`
- diagnostics directory paths
- `IPluginLog`
- current time provider if tests need deterministic freshness.

- [ ] **Step 4: Keep the old window compiling**

During this task only, let `CraftArchitectCompanionWindow` delegate quote operations to the new controller. This reduces migration risk before deleting the window.

- [ ] **Step 5: Verify focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbenchControllerTests|CraftAppraisalPanelPresenterTests|WorkshopHostCraftQuoteProviderTests|LastGoodCraftQuoteProviderTests" --no-restore
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchController.cs src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchControllerTests.cs
git commit -m "refactor: extract craft appraisal controller"
```

## Task 3: Mount Craft Appraisal In Acquisition Workbench

**Files:**

- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
- Create or modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalPanelRenderer.cs`
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs`

- [ ] **Step 1: Write failing integration tests**

Cover selected-line quote request mapping and threshold apply.

```csharp
[Fact]
public void BuildQuoteRequest_UsesSelectedWorkbenchLine()
{
    var line = new MarketAcquisitionQuickShopLineDraft
    {
        ItemId = 5060,
        ItemName = "Darksteel Ingot",
        QuantityMode = "TargetQuantity",
        TargetQuantity = 3,
        HqPolicy = "Either",
        MaxUnitPrice = 1500,
    };

    var request = CraftAppraisalWorkbenchRequestBuilder.Build(line, "North America");

    Assert.Equal(5060u, request.ItemId);
    Assert.Equal("Darksteel Ingot", request.ItemName);
    Assert.Equal(3u, request.Quantity);
    Assert.Equal(1500u, request.BuyThresholdUnitPrice);
}

[Fact]
public void ApplyQuoteThreshold_UpdatesOnlySelectedLine()
{
    var draft = TestDraft.WithTwoLines();
    var updated = AcquisitionWorkbenchDraftMutation.ApplyMaxUnitPrice(draft, selectedLineIndex: 1, maxUnitPrice: 1200);

    Assert.NotEqual(1200u, updated.Lines[0].MaxUnitPrice);
    Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
}
```

- [ ] **Step 2: Add workbench quote dependencies**

Update `AcquisitionWorkbenchWindow` constructor to receive a `CraftAppraisalWorkbenchController` or a factory for it. Avoid constructing the provider chain in multiple windows.

- [ ] **Step 3: Render quote controls in `DrawAppraisePane`**

In `DrawAppraisePane`, keep the existing line selector and stock availability. Insert the quote block between selected-line summary and stock controls:

- `Fetch Craft Quote`
- quote source/cost/freshness text
- calculation details
- `Use Craft Cost As Threshold`
- `Clear Quote Evidence`

- [ ] **Step 4: Apply quote cost explicitly**

Implement the action so it updates `draft.Lines[selectedLineIndex].MaxUnitPrice`, clears stock availability for that line, and leaves quote evidence visible.

- [ ] **Step 5: Remove percentage threshold actions**

Do not port the `-10%` and `+10%` buttons from `CraftArchitectCompanionWindow`.

- [ ] **Step 6: Verify focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbenchIntegrationTests|StockAvailabilityPanelPresenterTests|AcquisitionWorkbenchStockRequestBuilderTests" --no-restore
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalPanelRenderer.cs src/MarketMafioso/Windows/MainWindow.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs
git commit -m "feat: integrate craft appraisal into acquisition workbench"
```

## Task 4: Move Quote Diagnostics Into Diagnostics

**Files:**

- Modify: `src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalDiagnosticsSnapshotTests.cs`

- [ ] **Step 1: Write failing diagnostics tests**

Build snapshot tests for provider status and diagnostic printout paths.

```csharp
[Fact]
public void Snapshot_IncludesLastQuoteStatusAndPrintout()
{
    var state = CraftAppraisalWorkbenchState.CreateForTests();
    state.WorkshopHostEnabled = true;
    state.WorkshopHostAvailable = true;
    state.WorkshopHostStatus = "craft.appraise available";
    state.CraftQuoteStatus = "Craft quote refreshed.";
    state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

    var snapshot = state.CreateDiagnosticsSnapshot();

    Assert.True(snapshot.WorkshopHostEnabled);
    Assert.True(snapshot.WorkshopHostAvailable);
    Assert.Equal("quote.log", snapshot.LastCraftQuoteDiagnosticFilePath);
    Assert.Equal("Craft quote refreshed.", snapshot.CraftQuoteStatus);
}
```

- [ ] **Step 2: Pass snapshot into diagnostics window**

Add a `Func<CraftAppraisalDiagnosticsSnapshot>` to `MarketAcquisitionDiagnosticsWindow`.

- [ ] **Step 3: Render a `Craft Quote Diagnostics` section**

Show compact rows:

- Workshop Host quote API status;
- capability last checked;
- last quote status;
- last quote item;
- last-good/cache label;
- quote printout path;
- market-depth printout path.

- [ ] **Step 4: Verify focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalDiagnosticsSnapshotTests|MarketAcquisitionRouteDiagnosticsTests" --no-restore
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs src/MarketMafioso/Windows/MainWindow.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalDiagnosticsSnapshotTests.cs
git commit -m "feat: surface craft quote diagnostics in diagnostics window"
```

## Task 5: Remove Standalone CA Companion Surface

**Files:**

- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
- Modify: `src/MarketMafioso/Plugin.cs`
- Delete: `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`
- Modify tests under `tests/MarketMafioso.Tests/CraftArchitectCompanion/*`

- [ ] **Step 1: Search remaining window references**

Run:

```powershell
rg -n "CraftArchitectCompanionWindow|CA Companion|Open Companion|CraftArchitectCompanionModuleSummary|CraftArchitectCompanion" src tests
```

Expected before deletion: references in `MainWindow`, `Plugin`, and tests.

- [ ] **Step 2: Remove the top-level tab and property**

In `MainWindow`:

- remove `CraftArchitectCompanionModuleSummary`;
- remove `CraftArchitectCompanion = new CraftArchitectCompanionWindow(...)`;
- remove `public CraftArchitectCompanionWindow CraftArchitectCompanion { get; }`;
- remove the `CA Companion` tab branch;
- remove `DrawCraftArchitectCompanionTab`;
- update overview/module copy so Craft Architect quote evidence is described under Market Acquisition, not as a module.

- [ ] **Step 3: Remove window registration**

In `Plugin.cs`, remove:

```csharp
windowSystem.AddWindow(mainWindow.CraftArchitectCompanion);
```

- [ ] **Step 4: Delete the standalone window**

Delete `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs` after all behavior has moved to workbench/controller/diagnostics.

- [ ] **Step 5: Verify no product-surface references remain**

Run:

```powershell
rg -n "CA Companion|Open Companion|CraftArchitectCompanionWindow" src tests docs
```

Expected: no source references. Historical docs may still mention the old term, but new user-facing docs should not.

- [ ] **Step 6: Run focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftArchitectCompanion|AcquisitionWorkbench|MarketAcquisitionDiagnostics" --no-restore
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/MarketMafioso/Windows/MainWindow.cs src/MarketMafioso/Plugin.cs src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs tests/MarketMafioso.Tests/CraftArchitectCompanion tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench
git commit -m "refactor: remove standalone craft architect companion surface"
```

## Task 6: Final Verification And Docs Cleanup

**Files:**

- Modify: `docs/workshop-host.md`
- Modify: `docs/hosted-receiver.md`
- Modify: `docs/self-hosting.md`
- Modify: `docs/design/2026-07-05-client-acquisition-surface-reconciliation-plan.md` if needed to point at the superseding design.

- [ ] **Step 1: Update docs language**

Replace user-facing language that says MMF's `Craft Architect Companion` is the normal quote consumer. Use:

- `Acquisition Workbench craft appraisal`
- `Craft Architect quote evidence`
- `Workshop Host craft quote API`

- [ ] **Step 2: Run documentation/source search**

Run:

```powershell
rg -n "Craft Architect Companion|CA Companion|CraftArchitectCompanionWindow|Open Companion" docs src tests
```

Expected: only historical design/archive references or service namespace references remain.

- [ ] **Step 3: Run focused test suites**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftArchitectCompanion|AcquisitionWorkbench|MarketAcquisitionDiagnostics|ObservedMarketSnapshotCacheTests" --no-restore
```

Expected: pass.

- [ ] **Step 4: Build solution**

Run:

```powershell
dotnet build .\MarketMafioso.sln --no-restore
```

Expected: `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 5: Manual plugin smoke**

Use the dev plugin deployment path already established for this repo:

```powershell
.\tools\Deploy-DevPlugin.ps1
```

In game, verify:

- no `CA Companion` tab appears;
- Acquisition Workbench Appraise pane can fetch quote evidence;
- `Use Craft Cost As Threshold` updates only the selected line;
- stock availability refreshes after threshold edits;
- Diagnostics shows quote provider status and diagnostic paths;
- route sync/prepare/start remains available without using the dashboard.

- [ ] **Step 6: Commit docs cleanup**

```powershell
git add docs src tests
git commit -m "docs: update craft appraisal workbench language"
```

## Self-Review Checklist

- Every design requirement maps to a task:
  - workbench owns user journey: Tasks 3 and 5;
  - diagnostics owns provider visibility: Task 4;
  - service code remains: Tasks 1 and 2;
  - standalone surface removed: Task 5;
  - docs updated: Task 6.
- No task makes CA cost authoritative.
- No task depends on dashboard claim/accept for client-authored routes.
- No task introduces percentage threshold adjustment buttons.
- The plan keeps `AllBelowThreshold` stock-depth semantics from the acquisition reconciliation spec.
- The plan leaves Workshop Host API semantics unchanged.
