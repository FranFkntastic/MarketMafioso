# Market Acquisition Route Runner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the temporary guided route buttons with a stateful Market Acquisition route runner and optional route diagnostics.

**Architecture:** Keep the existing `MarketAcquisitionGuidedRouteSession` as the stop model, add a `MarketAcquisitionRouteRunner` to own lifecycle/control semantics, and add `MarketAcquisitionRouteDiagnostics` for optional file-backed route traces. `MainWindow` drives the runner from framework ticks and displays start/pause/stop/restart controls.

**Tech Stack:** C# 12, Dalamud `IFramework`, ImGui, xUnit plugin tests.

---

### Task 1: Diagnostics

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteDiagnosticsTests.cs`

- [ ] **Step 1: Write failing tests**

Add tests for enabled log creation, event writing, completion, and disabled mode ignoring records.

- [ ] **Step 2: Implement diagnostics**

Mirror the workshop diagnostics shape with route-specific file names and start metadata.

- [ ] **Step 3: Verify focused diagnostics tests**

Run: `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteDiagnosticsTests" -v minimal`

### Task 2: Route Runner

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`

- [ ] **Step 1: Write failing lifecycle tests**

Cover start, pause, resume, stop, restart, automatic active-stop command execution, and stop recording.

- [ ] **Step 2: Implement runner**

Use `MarketAcquisitionGuidedRouteSession` internally. Expose state, status message, active stop, stops, diagnostics path, control methods, and progression methods consumed by `MainWindow`.

- [ ] **Step 3: Verify focused runner tests**

Run: `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteRunnerTests" -v minimal`

### Task 3: Plugin UI Wiring

**Files:**
- Modify: `MarketMafioso/Plugin.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Pass route diagnostics directory**

Pass `Path.Combine(PluginInterface.GetPluginConfigDirectory(), "market-acquisition-route-logs")` into `MainWindow`.

- [ ] **Step 2: Replace route controls**

Replace `Start Guided Route`, `Reset Route`, command textbox, copy command, and execute command buttons with `Start Route`, `Start With Diagnostics`, `Pause`/`Resume`, `Stop`, and `Restart`.

- [ ] **Step 3: Move route progression through runner**

Make framework ticks call the runner pipeline: execute pending travel, handle current-world transitions, submit search, run live probe, and record live candidate results.

### Task 4: Verification

**Files:**
- All changed files.

- [ ] **Step 1: Run build**

Run: `dotnet build "MarketMafioso.sln" -c Debug`

- [ ] **Step 2: Run plugin tests**

Run: `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal`

- [ ] **Step 3: Run server tests**

Run: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal`

- [ ] **Step 4: Run format check**

Run: `dotnet format "MarketMafioso.sln" --verify-no-changes`

- [ ] **Step 5: Deploy plugin**

Run: `MarketMafioso/tools/Deploy-DevPlugin.ps1 -TargetDll "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\_deployed\MarketMafioso\MarketMafioso.dll"`
