# Workshop Assembly Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional workshop assembly diagnostic run mode that writes a timestamped trace file without changing assembly behavior.

**Architecture:** Keep diagnostics as an optional recorder owned by the runner. UI automation emits lifecycle/action events through the runner, and the normal assembly path remains clean when diagnostics are not enabled.

**Tech Stack:** C# 12, Dalamud plugin services, xUnit, ImGui.NET.

---

## File Structure

- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyDiagnostics.cs`: file-backed diagnostic recorder plus a no-op mode.
- Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyDiagnosticsTests.cs`: pure tests for file creation, event content, elapsed timing, and disposal.
- Modify `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`: accept optional diagnostics on start, record state/action/lockout/failure/completion events, expose last diagnostic file path.
- Modify `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`: accept optional diagnostics sink and record request lifecycle/action details.
- Modify `MarketMafioso/Plugin.cs`: pass a workshop diagnostics directory into the runner.
- Modify `MarketMafioso/Windows/MainWindow.cs`: add `Start Assembly With Diagnostics` and report the output path.

## Task 1: Recorder

- [ ] Write tests showing the recorder creates a timestamped file under the requested directory and records start/event/end lines.
- [ ] Run the focused tests and confirm they fail because `WorkshopAssemblyDiagnostics` does not exist.
- [ ] Implement the recorder with `CreateEnabled`, `Disabled`, `Record`, `Complete`, `Fail`, `FilePath`, and `Dispose`.
- [ ] Run the focused tests and confirm they pass.

## Task 2: Runner Integration

- [ ] Add optional diagnostic startup to `WorkshopAssemblyRunner.Start(plan, enableDiagnostics: bool)`.
- [ ] Record state transitions, pending/action results, lockout start, completion, stop, and failure.
- [ ] Expose `LastDiagnosticFilePath` for UI reporting.
- [ ] Keep normal runs on the no-op recorder.

## Task 3: UI Automation Events

- [ ] Give `WorkshopAssemblyUiAutomation` a settable diagnostics sink.
- [ ] Record menu selections, confirmation selections, request setup, request icon selection, request refresh confirmation, and tracked UI state on pending waits.
- [ ] Avoid adding sleeps or changing action order.

## Task 4: UI Wiring

- [ ] Add `Start Assembly With Diagnostics` beside the normal native assembly button.
- [ ] Reuse the same preflight path for both buttons.
- [ ] After a diagnostic run starts, show the output path in `workshopStatus`.

## Task 5: Verification

- [ ] Run focused plugin tests for diagnostics.
- [ ] Run all plugin tests.
- [ ] Build the plugin in Debug.
- [ ] Run format verification.
- [ ] Deploy the dev plugin and verify the target DLL hash.
