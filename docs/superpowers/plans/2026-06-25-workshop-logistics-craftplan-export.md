# Workshop Logistics Craft Architect Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the visible Workshop Prep module to Workshop Logistics and make its Craft Architect export produce native `.craftplan` JSON instead of Teamcraft-style text.

**Architecture:** Keep existing `WorkshopPrep` namespaces, persisted config keys, and queue services for compatibility. Change only the user-facing labels and the manifest export boundary. Emit Craft Architect's native plan wrapper shape directly from MarketMafioso DTOs so the plugin does not depend on Craft Architect binaries.

**Tech Stack:** C# 12, System.Text.Json, Dalamud ImGui, xUnit, existing `WorkshopPrep` export tests.

---

## File Structure

- Modify `MarketMafioso/WorkshopPrep/WorkshopMaterialManifestExportService.cs`
  - Replace Teamcraft text generation for Craft Architect with native plan JSON.
  - Keep Artisan export unchanged.
  - Add private DTOs that mirror Craft Architect's `PlanSerializationWrapper` / `SerializablePlanNode` properties.
- Modify `MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialManifestExportServiceTests.cs`
  - Replace Teamcraft text assertions with JSON assertions for version, plan name, root IDs, nodes, quantities, and source metadata.
- Modify `MarketMafioso/Windows/MainWindow.cs`
  - Rename visible module/tab/header copy from Workshop Prep to Workshop Logistics.
  - Rename export action from `Copy Craft Architect Import` to `Copy Craft Architect Plan`.
  - Update export helper text to say Craft Architect `.craftplan` JSON.
- Modify `docs/design/2026-06-23-workshop-material-manifest-export.md`
  - Correct Craft Architect export design from Teamcraft text to native `.craftplan` JSON.
- Modify prior implementation plans that explicitly preserve the old plain-text CA assumption:
  - `docs/superpowers/plans/2026-06-24-workshop-saved-jobs-browser-and-eta.md`
  - `docs/superpowers/plans/2026-06-24-workshop-action-layout.md`

## Tasks

### Task 1: Tests For Native Craft Architect Plan Export

**Files:**
- Modify: `MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialManifestExportServiceTests.cs`

- [ ] Replace `ExportCraftArchitectManifest_UsesCraftArchitectTeamcraftTextImportFormat` with `ExportCraftArchitectManifest_UsesNativeCraftPlanJsonFormat`.
- [ ] Parse `result.Content` with `JsonDocument.Parse`.
- [ ] Assert:
  - `Version == 2`
  - `Name == "Workshop Materials - Shark-class Pressure Hull x16 + 1 more - Inventory Missing - 2026-06-23 2115"`
  - `DataCenter == ""`, `World == ""`
  - `RootNodeIds == ["mmf-5378", "mmf-6000"]`
  - node `mmf-5378` has `ItemId=5378`, `Name="Cobalt Ingot"`, `IconId=20`, `Quantity=288`, `Source=3`, `SourceReason=0`, `CanBuyFromMarket=true`, `CanCraft=false`, `ChildNodeIds=[]`
  - node `mmf-6000` has `Quantity=32`
- [ ] Update `ExportCraftArchitectManifest_TotalMissingMode_ExportsOnlyUnownedQuantity` to assert one JSON node for Darksteel Ore and no Cobalt Ingot.
- [ ] Run:
  - `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopMaterialManifestExportServiceTests" -v minimal`
  - Expected before implementation: failing assertions that current export is not JSON.

### Task 2: Native Craft Architect Plan Export

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopMaterialManifestExportService.cs`

- [ ] Rename the public static Craft Architect export method to `ExportCraftArchitectPlan`, leaving no UI call sites on the old semantic name.
- [ ] Add JSON options for Craft Architect with `WriteIndented = true`.
- [ ] Replace `BuildTeamcraftImportText` with `BuildCraftArchitectPlanJson`.
- [ ] Emit one root node per missing material:
  - `NodeId = $"mmf-{itemId}"`
  - `ParentNodeId = null`
  - `ChildNodeIds = []`
  - `Source = 3` for Craft Architect `AcquisitionSource.UnknownSource`
  - `SourceReason = 0` for Craft Architect `AcquisitionSourceReason.SystemDefault`
  - `CanBuyFromMarket = true`
  - `CanCraft = false`
  - `Yield = 1`
  - `SelectedVendorIndex = -1`
- [ ] Return message `Copied Craft Architect .craftplan JSON: {materials.Count} materials.`
- [ ] Run the focused export tests and verify they pass.

### Task 3: UI Copy And Module Naming

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] Change visible module naming to `Workshop Logistics`:
  - tab label
  - overview module row
  - module header
  - current modules summary
  - module summary constant
- [ ] Keep method/class/namespace names as `WorkshopPrep` to avoid broad persisted-state churn.
- [ ] Change export menu item to `Copy Craft Architect Plan`.
- [ ] Change `CopyWorkshopCraftArchitectManifest` to call `ExportCraftArchitectPlan`.
- [ ] Change helper text to `Handoff contains VIWI and future queue targets. Export contains Artisan JSON and Craft Architect .craftplan JSON.`
- [ ] Run plugin tests or full solution tests after the service/UI compile together.

### Task 4: Documentation Correction

**Files:**
- Modify: `docs/design/2026-06-23-workshop-material-manifest-export.md`
- Modify: `docs/superpowers/plans/2026-06-24-workshop-saved-jobs-browser-and-eta.md`
- Modify: `docs/superpowers/plans/2026-06-24-workshop-action-layout.md`

- [ ] Replace claims that Craft Architect export is Teamcraft-style/plain-text with native `.craftplan` JSON.
- [ ] Preserve the stretch-goal idea as `rich MarketMafioso manifest` only if it is clearly separate from Craft Architect native plan export.
- [ ] Make old plans explicitly note that the CA format was superseded on 2026-06-25.

### Task 5: Verification, Deployment, Commit

**Files:**
- All files touched above.

- [ ] Run `dotnet test "MarketMafioso.sln" -c Debug -v minimal`.
- [ ] Run `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- [ ] Run `MarketMafioso/tools/Deploy-DevPlugin.ps1`.
- [ ] Verify deploy script reports the configured Dalamud DLL target and a new visible version.
- [ ] Review `git diff`.
- [ ] Commit with message `feat: export workshop logistics plans for craft architect`.
