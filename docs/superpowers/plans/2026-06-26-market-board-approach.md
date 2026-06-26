# Market Board Approach Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a route step that opens nearby market boards directly, and uses optional vnavmesh IPC only when the board is nearby but outside direct interaction range.

**Architecture:** Add a focused `MarketBoardApproachService` for market board discovery, direct interaction, and vnavmesh approach decisions. Add `VNavmeshIpc` as an optional IPC adapter. Wire the service into `MainWindow.MonitorGuidedRoute()` before item search.

**Tech Stack:** C# 12, Dalamud API 15, FFXIVClientStructs direct object interaction, optional vnavmesh IPC.

---

### Task 1: Route Approach Models And Decision Tests

**Files:**
- Create: `MarketMafioso.Tests/MarketAcquisition/MarketBoardApproachServiceTests.cs`
- Create: `MarketMafioso/MarketAcquisition/MarketBoardApproachService.cs`

- [ ] Write tests proving direct interaction is selected before vnavmesh.
- [ ] Write tests proving vnavmesh is selected only outside direct range and inside approach range.
- [ ] Implement `MarketBoardApproachDecision`, `MarketBoardApproachResult`, and static decision logic.
- [ ] Run `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardApproachServiceTests" -v minimal`.

### Task 2: Optional vnavmesh IPC Adapter

**Files:**
- Create: `MarketMafioso.Tests/MarketAcquisition/VNavmeshIpcTests.cs`
- Create: `MarketMafioso/MarketAcquisition/VNavmeshIpc.cs`

- [ ] Write adapter tests with a fake adapter for unavailable, ready, running, accepted, and rejected movement requests.
- [ ] Implement `IVNavmeshIpcAdapter`, `VNavmeshIpc`, and `DalamudVNavmeshIpcAdapter`.
- [ ] Run `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~VNavmeshIpcTests" -v minimal`.

### Task 3: Runtime Market Board Approach

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardApproachService.cs`
- Modify: `MarketMafioso/Plugin.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] Add runtime market board object discovery by name and targetable object kind.
- [ ] Add direct interaction through `TargetSystem.InteractWithObject`.
- [ ] Add optional vnavmesh approach when direct interaction is not yet available.
- [ ] Wire the service before `MarketBoardItemSearchDriver.Search`.
- [ ] Run plugin tests and format verification.

### Task 4: Deploy

**Files:**
- Modify: committed files from Tasks 1-3.

- [ ] Commit the implementation.
- [ ] Push `local-dev`.
- [ ] Run `MarketMafioso/tools/Deploy-DevPlugin.ps1` against the dedicated deployed DLL path.
