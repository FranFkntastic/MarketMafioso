# Acquisition Workbench Appraisal Before Threshold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users add acquisition intent lines and fetch Craft Architect craft-cost evidence before choosing a max unit price or gil cap.

**Architecture:** Treat workbench lines as acquisition intents first and route-executable purchase constraints second. Keep `MaxUnitPrice = 0` as the existing unset value, display it as `Unset`, allow appraisal on unset-threshold lines, and continue blocking stock checks and route sync until a real max unit price exists.

**Tech Stack:** C#/.NET, Dalamud ImGui, xUnit, existing Market Acquisition draft, validator, workbench, stock availability, and Craft Architect appraisal services.

---

## Source Map

- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
  - Relax `CanAddLine`.
  - Display unset max unit price honestly.
  - Add selected-line pricing inputs for max unit price and optional gil cap.
  - Gate stock check on route readiness, not appraisal readiness.
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidator.cs`
  - Provide a pure, testable rule for enabling `Add Line` with an unset threshold.
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchDraftMutation.cs`
  - Keep `ApplyMaxUnitPrice`.
  - Add `ApplyPricing`.
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchRequestBuilder.cs`
  - Ensure unset max unit price and gil cap do not block quote request building.
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenter.cs`
  - Report that stock availability needs a max unit price when selected line threshold is unset.
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
  - Expose `EnableWorkshopHostCraftQuotes` in the existing `Craft Quote Evidence` settings block.
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenterTests.cs`
- Test: create `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidatorTests.cs`
- Test: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopDraftValidatorTests.cs`
- Test: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs` also receives pricing mutation coverage to keep the patch small.

## Task 1: Define Incomplete-Line Route Readiness

**Files:**
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopDraftValidatorTests.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraftValidator.cs`

- [ ] **Step 1: Write the validator test for an appraisal-ready but route-incomplete line**

Add this test to `MarketAcquisitionQuickShopDraftValidatorTests`:

```csharp
[Fact]
public void Validate_RejectsUnsetMaxUnitPriceOnlyAtRouteSyncBoundary()
{
    var draft = ValidDraft() with
    {
        Lines =
        [
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 5060,
                ItemName = "Darksteel Ingot",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 3,
                HqPolicy = "Either",
                MaxUnitPrice = 0,
                GilCap = 0,
            },
        ],
    };

    var result = MarketAcquisitionQuickShopDraftValidator.Validate(
        draft,
        "client-secret",
        "Wei Ning",
        "Siren");

    Assert.False(result.IsValid);
    Assert.Contains("Line 1: max unit price is required before route sync.", result.Errors);
}
```

- [ ] **Step 2: Run the focused validator test and confirm it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionQuickShopDraftValidatorTests.Validate_RejectsUnsetMaxUnitPriceOnlyAtRouteSyncBoundary" --no-restore
```

Expected: fail because the existing error text is `Line 1: max unit price is required.`

- [ ] **Step 3: Update the validator message to describe route sync readiness**

In `MarketAcquisitionQuickShopDraftValidator.ValidateLine`, replace:

```csharp
if (line.MaxUnitPrice == 0)
    errors.Add($"Line {lineNumber}: max unit price is required.");
```

with:

```csharp
if (line.MaxUnitPrice == 0)
    errors.Add($"Line {lineNumber}: max unit price is required before route sync.");
```

- [ ] **Step 4: Update the broad bad-draft assertion**

In `Validate_ReturnsAllRelevantErrorsForBadDraft`, replace:

```csharp
Assert.Contains("Line 1: max unit price is required.", result.Errors);
```

with:

```csharp
Assert.Contains("Line 1: max unit price is required before route sync.", result.Errors);
```

- [ ] **Step 5: Run the validator tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionQuickShopDraftValidatorTests" --no-restore
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraftValidator.cs tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionQuickShopDraftValidatorTests.cs
git commit -m "test: clarify acquisition route readiness validation"
```

## Task 2: Allow Adding Intent Lines Without A Threshold

**Files:**
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidator.cs`
- Create: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidatorTests.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`

- [ ] **Step 1: Write line-input validator tests**

Create `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidatorTests.cs`:

```csharp
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class AcquisitionWorkbenchLineInputValidatorTests
{
    [Fact]
    public void CanAddIntentLine_AllowsBlankMaxUnitPrice()
    {
        var result = AcquisitionWorkbenchLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "3",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.True(result);
    }

    [Fact]
    public void CanAddIntentLine_RejectsNonNumericMaxUnitPrice()
    {
        var result = AcquisitionWorkbenchLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "3",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "abc",
            gilCapBuffer: "");

        Assert.False(result);
    }

    [Fact]
    public void CanAddIntentLine_RequiresTargetQuantityForTargetMode()
    {
        var result = AcquisitionWorkbenchLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.False(result);
    }

    [Fact]
    public void CanAddIntentLine_AllowsBlankMaxQuantityForAllBelowThreshold()
    {
        var result = AcquisitionWorkbenchLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "AllBelowThreshold",
            targetQuantityBuffer: "",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.True(result);
    }
}
```

- [ ] **Step 2: Run the validator tests and confirm they fail**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "AcquisitionWorkbenchLineInputValidatorTests" --no-restore
```

Expected: fail because `AcquisitionWorkbenchLineInputValidator` does not exist.

- [ ] **Step 3: Add the line-input validator**

Create `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidator.cs`:

```csharp
namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class AcquisitionWorkbenchLineInputValidator
{
    public static bool CanAddIntentLine(
        AcquisitionItemOption? selectedItem,
        string quantityMode,
        string targetQuantityBuffer,
        string maxQuantityBuffer,
        string maxUnitPriceBuffer,
        string gilCapBuffer) =>
        selectedItem is not null &&
        (string.IsNullOrWhiteSpace(maxUnitPriceBuffer) ||
         TryParseUInt(maxUnitPriceBuffer, out _)) &&
        (!string.Equals(quantityMode, "TargetQuantity", StringComparison.OrdinalIgnoreCase) ||
         TryParseUInt(targetQuantityBuffer, out var targetQuantity) && targetQuantity > 0) &&
        (string.IsNullOrWhiteSpace(gilCapBuffer) || TryParseUInt(gilCapBuffer, out _)) &&
        (string.IsNullOrWhiteSpace(maxQuantityBuffer) || TryParseUInt(maxQuantityBuffer, out _));

    private static bool TryParseUInt(string value, out uint parsed) =>
        uint.TryParse(value?.Trim(), out parsed);
}
```

- [ ] **Step 4: Change `CanAddLine` to use the validator**

In `AcquisitionWorkbenchWindow.CanAddLine`, replace the method with:

```csharp
private bool CanAddLine() =>
    AcquisitionWorkbenchLineInputValidator.CanAddIntentLine(
        ResolveSelectedItem(),
        QuantityModes[quantityModeIndex],
        targetQuantityBuffer,
        maxQuantityBuffer,
        maxUnitPriceBuffer,
        gilCapBuffer);
```

This allows blank `Max Unit Price` to become `0`, while still rejecting non-numeric input.

- [ ] **Step 5: Keep `AddLineFromBuffers` storage unchanged**

Confirm `AddLineFromBuffers` still parses blank max unit price as `0`:

```csharp
_ = TryParseUInt(maxUnitPriceBuffer, out var maxUnitPrice);
```

Leave the assignment unchanged:

```csharp
MaxUnitPrice = maxUnitPrice,
```

- [ ] **Step 6: Run line-input validator tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "AcquisitionWorkbenchLineInputValidatorTests" --no-restore
```

Expected: pass.

- [ ] **Step 7: Run a compile check**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 8: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidator.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/AcquisitionWorkbenchLineInputValidatorTests.cs
git commit -m "feat: allow acquisition intent lines before threshold"
```

## Task 3: Display Unset Pricing Instead Of Zero Gil

**Files:**
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`

- [ ] **Step 1: Add a threshold formatter near `FormatGil`**

In `AcquisitionWorkbenchWindow`, replace:

```csharp
private static string FormatGil(uint gil) => $"{gil:N0} gil";
```

with:

```csharp
private static string FormatGil(uint gil) => $"{gil:N0} gil";

private static string FormatOptionalGil(uint gil) =>
    gil == 0 ? "Unset" : FormatGil(gil);
```

- [ ] **Step 2: Use the optional formatter in selected-line summary**

In `DrawSelectedLineSummary`, replace:

```csharp
ImGui.TextColored(ColMuted, $"Max unit: {FormatGil(selected.MaxUnitPrice)}");
```

with:

```csharp
ImGui.TextColored(ColMuted, $"Max unit: {FormatOptionalGil(selected.MaxUnitPrice)}");
```

- [ ] **Step 3: Use the optional formatter in queued lines**

In `DrawQueuedLines`, replace:

```csharp
ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
```

with:

```csharp
ImGui.TextUnformatted(FormatOptionalGil(line.MaxUnitPrice));
```

- [ ] **Step 4: Run a compile check**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs
git commit -m "fix: show unset acquisition thresholds honestly"
```

## Task 4: Keep Craft Appraisal Available Before Threshold

**Files:**
- Modify: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchRequestBuilder.cs`

- [ ] **Step 1: Add a quote request test for an unset-threshold line**

Add this test to `CraftAppraisalWorkbenchIntegrationTests`:

```csharp
[Fact]
public void BuildQuoteRequest_AllowsUnsetThresholdForCraftAppraisal()
{
    var draft = TestDraft.WithLine(new MarketAcquisitionQuickShopLineDraft
    {
        ItemId = 5060,
        ItemName = "Darksteel Ingot",
        QuantityMode = "TargetQuantity",
        TargetQuantity = 3,
        HqPolicy = "Either",
        MaxUnitPrice = 0,
        GilCap = 0,
    });

    var request = CraftAppraisalWorkbenchRequestBuilder.Build(draft, draft.Lines[0]);

    Assert.Equal(5060u, request.ItemId);
    Assert.Equal(3u, request.Quantity);
    Assert.Equal(0u, request.BuyThresholdUnitPrice);
    Assert.Equal(0u, request.GilCap);
}
```

- [ ] **Step 2: Run the focused test**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbenchIntegrationTests.BuildQuoteRequest_AllowsUnsetThresholdForCraftAppraisal" --no-restore
```

Expected: pass with the current request builder because it already treats `0` as a normal value.

- [ ] **Step 3: Confirm the builder only guards item identity**

The top of `CraftAppraisalWorkbenchRequestBuilder.Build` should keep this guard:

```csharp
if (line.ItemId == 0)
    throw new InvalidOperationException("Selected line must have an item id before craft appraisal.");
```

It must not add a `MaxUnitPrice == 0` guard.

- [ ] **Step 4: Run workbench appraisal tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftAppraisalWorkbench" --no-restore
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchRequestBuilder.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs
git commit -m "test: allow craft appraisal before threshold"
```

## Task 5: Add Selected-Line Pricing Mutation

**Files:**
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchDraftMutation.cs`
- Modify: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs` or create `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/AcquisitionWorkbenchDraftMutationTests.cs`

- [ ] **Step 1: Write mutation tests**

If keeping the tests in `CraftAppraisalWorkbenchIntegrationTests`, add:

```csharp
[Fact]
public void ApplyPricing_UpdatesOnlySelectedLineAndAdvancesRevision()
{
    var draft = TestDraft.WithLines(
        new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 10,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            GilCap = 0,
        },
        new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 3,
            HqPolicy = "Either",
            MaxUnitPrice = 0,
            GilCap = 0,
        });

    var updated = AcquisitionWorkbenchDraftMutation.ApplyPricing(
        draft,
        selectedLineIndex: 1,
        maxUnitPrice: 1200,
        gilCap: 5000);

    Assert.Equal(100u, updated.Lines[0].MaxUnitPrice);
    Assert.Equal(0u, updated.Lines[0].GilCap);
    Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
    Assert.Equal(5000u, updated.Lines[1].GilCap);
    Assert.Equal(draft.DraftRevision + 1, updated.DraftRevision);
}
```

- [ ] **Step 2: Run the mutation test and confirm it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "ApplyPricing_UpdatesOnlySelectedLineAndAdvancesRevision" --no-restore
```

Expected: fail because `ApplyPricing` does not exist.

- [ ] **Step 3: Implement `ApplyPricing`**

In `AcquisitionWorkbenchDraftMutation`, add:

```csharp
public static MarketAcquisitionQuickShopDraft ApplyPricing(
    MarketAcquisitionQuickShopDraft draft,
    int selectedLineIndex,
    uint maxUnitPrice,
    uint gilCap)
{
    ArgumentNullException.ThrowIfNull(draft);
    if (selectedLineIndex < 0 || selectedLineIndex >= draft.Lines.Count)
        throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

    var lines = draft.Lines.ToList();
    lines[selectedLineIndex] = lines[selectedLineIndex] with
    {
        MaxUnitPrice = maxUnitPrice,
        GilCap = gilCap,
    };

    return draft.WithNextRevision() with { Lines = lines };
}
```

- [ ] **Step 4: Refactor `ApplyMaxUnitPrice` to call `ApplyPricing`**

Replace the body of `ApplyMaxUnitPrice` with:

```csharp
ArgumentNullException.ThrowIfNull(draft);
if (selectedLineIndex < 0 || selectedLineIndex >= draft.Lines.Count)
    throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

return ApplyPricing(
    draft,
    selectedLineIndex,
    maxUnitPrice,
    draft.Lines[selectedLineIndex].GilCap);
```

- [ ] **Step 5: Run mutation tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "ApplyMaxUnitPrice|ApplyPricing" --no-restore
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionWorkbenchDraftMutation.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/CraftAppraisalWorkbenchIntegrationTests.cs
git commit -m "feat: add workbench selected-line pricing mutation"
```

## Task 6: Add Selected-Line Pricing Controls

**Files:**
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`

- [ ] **Step 1: Add selected-line pricing buffers**

Near the existing line input buffers, add:

```csharp
private string selectedMaxUnitPriceBuffer = string.Empty;
private string selectedGilCapBuffer = string.Empty;
private int selectedPricingLineIndex = -1;
private int selectedPricingLineRevision;
```

- [ ] **Step 2: Add a sync helper for selected-line pricing buffers**

Add this helper near `ResolveSelectedLine`:

```csharp
private void SyncSelectedPricingBuffers(MarketAcquisitionQuickShopLineDraft selected)
{
    if (selectedPricingLineIndex == selectedLineIndex &&
        selectedPricingLineRevision == draft.DraftRevision)
    {
        return;
    }

    selectedPricingLineIndex = selectedLineIndex;
    selectedPricingLineRevision = draft.DraftRevision;
    selectedMaxUnitPriceBuffer = selected.MaxUnitPrice == 0 ? string.Empty : selected.MaxUnitPrice.ToString();
    selectedGilCapBuffer = selected.GilCap == 0 ? string.Empty : selected.GilCap.ToString();
}
```

- [ ] **Step 3: Add the selected-line pricing editor**

Add this method near `DrawCraftAppraisal`:

```csharp
private void DrawSelectedLinePricingEditor(MarketAcquisitionQuickShopLineDraft selected)
{
    SyncSelectedPricingBuffers(selected);

    ImGui.Spacing();
    ImGui.TextColored(ColHeader, "Route Pricing");
    ImGui.Separator();
    DrawInput("Selected Max Unit Price", ref selectedMaxUnitPriceBuffer);
    DrawInput("Selected Gil Cap", ref selectedGilCapBuffer);

    var maxUnitValid = string.IsNullOrWhiteSpace(selectedMaxUnitPriceBuffer) ||
                       TryParseUInt(selectedMaxUnitPriceBuffer, out _);
    var gilCapValid = string.IsNullOrWhiteSpace(selectedGilCapBuffer) ||
                      TryParseUInt(selectedGilCapBuffer, out _);

    if (!maxUnitValid)
        ImGui.TextColored(ColError, "Max unit price must be a whole number.");
    if (!gilCapValid)
        ImGui.TextColored(ColError, "Gil cap must be a whole number.");

    var canApply = maxUnitValid && gilCapValid;
    if (!ImGuiUi.Button("Apply Pricing", canApply))
        return;

    _ = TryParseUInt(selectedMaxUnitPriceBuffer, out var maxUnitPrice);
    _ = TryParseUInt(selectedGilCapBuffer, out var gilCap);
    var oldStockStateKey = BuildStockStateKey(selected);
    draft = AcquisitionWorkbenchDraftMutation.ApplyPricing(
        draft,
        selectedLineIndex,
        maxUnitPrice,
        gilCap);

    selectedPricingLineRevision = draft.DraftRevision;
    lock (stockStateGate)
        stockStates.Remove(oldStockStateKey);
}
```

- [ ] **Step 4: Call the editor from `DrawAppraisePane`**

In `DrawAppraisePane`, after `DrawCraftAppraisal(selected);`, add:

```csharp
if (selected is not null)
    DrawSelectedLinePricingEditor(selected);
```

- [ ] **Step 5: Update quote threshold application to sync pricing buffers**

At the end of `ApplyQuoteThresholdToSelectedLine`, after the draft mutation and stock-state invalidation, add:

```csharp
if (ResolveSelectedLine() is { } updatedSelected)
    SyncSelectedPricingBuffers(updatedSelected);
```

- [ ] **Step 6: Run a compile check**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs
git commit -m "feat: edit selected acquisition line pricing"
```

## Task 7: Gate Stock Availability On Threshold Readiness

**Files:**
- Modify: `tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenterTests.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenter.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`

- [ ] **Step 1: Add presenter test for unset threshold**

Add this test:

```csharp
[Fact]
public void Build_WhenSelectedLineHasUnsetThreshold_AsksForThresholdBeforeStockCheck()
{
    var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState
    {
        SelectedLine = CreateLine(maxUnitPrice: 0),
    });

    Assert.Equal("Set a max unit price", view.Headline);
    Assert.Equal(StockAvailabilityPanelSeverity.Muted, view.Severity);
    Assert.Contains("Stock availability needs a max unit price", view.Detail);
}
```

Change the local helper signature to:

```csharp
private static MarketAcquisitionQuickShopLineDraft CreateLine(
    string quantityMode = "TargetQuantity",
    uint targetQuantity = 1,
    uint maxQuantity = 0,
    uint maxUnitPrice = 100) =>
    new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        QuantityMode = quantityMode,
        TargetQuantity = targetQuantity,
        MaxQuantity = maxQuantity,
        HqPolicy = "Either",
        MaxUnitPrice = maxUnitPrice,
    };
```

- [ ] **Step 2: Run the presenter test and confirm it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "Build_WhenSelectedLineHasUnsetThreshold_AsksForThresholdBeforeStockCheck" --no-restore
```

Expected: fail because the presenter currently falls through to no-result stock copy.

- [ ] **Step 3: Update `StockAvailabilityPanelPresenter.Build`**

After the `SelectedLine is null` branch, add:

```csharp
if (state.SelectedLine.MaxUnitPrice == 0)
{
    return new StockAvailabilityPanelView
    {
        Headline = "Set a max unit price",
        Detail = "Stock availability needs a max unit price because only under-threshold listings count as route-available stock.",
        Severity = StockAvailabilityPanelSeverity.Muted,
    };
}
```

- [ ] **Step 4: Add a no-fetch guard inside `CheckStockAsync`**

In `AcquisitionWorkbenchWindow.CheckStockAsync`, after resolving `line` and before reading `scope`, add:

```csharp
if (line.MaxUnitPrice == 0)
{
    SetStockState(BuildStockStateKey(line, string.Empty), new WorkbenchStockState
    {
        ErrorMessage = "Set a max unit price before checking stock.",
    });
    return;
}
```

This prevents Universalis fetches even if a future caller bypasses the button-enabled state.

- [ ] **Step 5: Gate workbench stock buttons**

In `DrawAppraisePane`, replace the `canCheck` calculation with:

```csharp
var canCheck = selected is { MaxUnitPrice: > 0 } &&
               state?.IsFetching != true &&
               string.IsNullOrWhiteSpace(routeScopeError);
```

- [ ] **Step 6: Run stock availability tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "StockAvailabilityPanelPresenterTests|AcquisitionWorkbenchStockRequestBuilderTests" --no-restore
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/MarketMafioso/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenter.cs src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs tests/MarketMafioso.Tests/Windows/AcquisitionWorkbench/StockAvailabilityPanelPresenterTests.cs
git commit -m "fix: require threshold before stock availability"
```

## Task 8: Expose Workshop Host Craft Quote Toggle

**Files:**
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add the settings checkbox**

In `DrawCraftQuoteSettingsSection`, before the manual fallback checkbox, add:

```csharp
var enableWorkshopHostQuotes = config.EnableWorkshopHostCraftQuotes;
if (ImGui.Checkbox("Enable Workshop Host craft quotes", ref enableWorkshopHostQuotes))
{
    config.EnableWorkshopHostCraftQuotes = enableWorkshopHostQuotes;
    config.Save();
}

ImGui.TextColored(
    ColMuted,
    "Uses the configured Workshop Host service for advisory craft-cost evidence when the host advertises craft.appraise.");
```

- [ ] **Step 2: Keep the manual fallback copy below the normal path**

Confirm the existing manual fallback text remains after the new Workshop Host toggle:

```csharp
ImGui.TextColored(
    ColMuted,
    "Default off. Workshop Host should be the normal quote path; manual craft cost entry is only for local troubleshooting.");
```

- [ ] **Step 3: Run a compile check**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: expose workshop host craft quote setting"
```

## Task 9: Final Verification And Dev Plugin Deploy

**Files:**
- Verify all modified source and tests.

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionQuickShopDraftValidatorTests|AcquisitionWorkbenchLineInputValidatorTests|CraftAppraisalWorkbench|StockAvailabilityPanelPresenterTests|AcquisitionWorkbenchStockRequestBuilderTests" --no-restore
```

Expected: pass.

- [ ] **Step 2: Run full solution build**

Run:

```powershell
dotnet build .\MarketMafioso.sln --no-restore
```

Expected: build succeeds with `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Deploy dev plugin**

Run:

```powershell
.\src\MarketMafioso\tools\Deploy-DevPlugin.ps1
```

Expected: output reports the installed Dalamud dev plugin DLL path and SHA256.

- [ ] **Step 4: Manual in-game smoke**

In the plugin:

- Open `Market Acquisition`.
- Open `Acquisition Workbench`.
- Add an item line with item, quantity mode, quantity, and HQ while leaving `Max Unit Price` and `Gil Cap` blank.
- Confirm the line appears with `Max Unit` shown as `Unset`.
- Select the line and open `Appraise`.
- Confirm `Fetch Craft Quote` is available.
- Fetch a craft quote.
- Confirm `Check Stock` remains disabled until max unit price is set.
- Click `Use Craft Cost As Threshold`, or enter a manual max unit price in `Route Pricing`.
- Confirm `Check Stock` becomes available.
- Confirm `Sync Route` remains blocked until every line has a max unit price.
- Confirm `Enable Workshop Host craft quotes` appears in Settings under `Craft Quote Evidence`.

- [ ] **Step 5: Commit final cleanup if verification forced changes**

If verification required any source or test changes, commit them:

```powershell
git add src tests
git commit -m "fix: complete appraisal-before-threshold workflow"
```

## Self-Review

- Spec coverage:
  - Lines can be added before threshold: Tasks 1 and 2, including pure readiness tests for the old `Add Line` failure path.
  - UI displays unset threshold honestly: Task 3.
  - Craft appraisal works before threshold: Task 4.
  - User can set threshold after appraisal without rebuilding line: Tasks 5 and 6.
  - Stock and route sync stay blocked until threshold exists: Tasks 1 and 7, including a no-fetch guard inside `CheckStockAsync`.
  - Workshop Host quote path is discoverable: Task 8.
  - Verification and dev-plugin deployment are covered: Task 9.
- Placeholder scan: no empty implementation steps; every source change step includes concrete code or a concrete confirmation.
- Type consistency:
  - `MaxUnitPrice` and `GilCap` stay `uint`.
  - Existing `0` value is reused only as unset state and rendered as `Unset`.
  - `CraftAppraisalWorkbenchRequestBuilder.Build` remains item-identity guarded, not threshold guarded.

## Agent Review

Subagent review by Bohr was read-only and found four actionable issues:

- Fixed a compile-risk typo in Task 7 by naming the real stock availability panel view return type.
- Strengthened Task 7 with an early `CheckStockAsync` guard so unset thresholds cannot trigger Universalis fetches through a future non-button path.
- Strengthened Task 2 with `AcquisitionWorkbenchLineInputValidator` and focused tests for the original failure path: adding an acquisition intent line while max unit price is blank.
- Corrected the manual smoke test from the old quick-shop submit label to the Acquisition Workbench `Sync Route` label.
