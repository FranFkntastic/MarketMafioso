# Market Acquisition Route Engine Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract Market Acquisition route execution from `MainWindow` into a headless, MMF-local `MarketAcquisitionRouteEngine` while preserving route behavior, diagnostics, reporting, and in-game UI.

**Architecture:** Build the engine locally in `src/MarketMafioso/MarketAcquisition` first. The engine owns route ticking, probing, visible-listing continuation, purchase passes, purchase monitoring, route counters, progress/audit reporting, and visit evidence through narrow ports. `MainWindow` keeps tab composition, dashboard request pickup/claim/preparation, and UI rendering through engine snapshots.

**Tech Stack:** C#/.NET 10, Dalamud.NET.Sdk 15, xUnit, existing MarketMafioso automation and Market Acquisition services, PowerShell verification on Windows.

---

## Source Context

- Approved spec: `docs/superpowers/specs/2026-07-10-market-acquisition-route-engine-extraction.md`
- Current conductor: `src/MarketMafioso/Windows/MainWindow.cs`
- Current route session/diagnostics owner: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Existing purchase state machine: `src/MarketMafioso/MarketAcquisition/MarketBoardAutomationController.cs`
- Existing purchase session: `src/MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
- Existing purchase executor: `src/MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs`
- Existing listing accumulator: `src/MarketMafioso/MarketAcquisition/MarketBoardListingReadAccumulator.cs`
- Existing route tests: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`
- Existing purchase tests: `tests/MarketMafioso.Tests/MarketAcquisition/MarketBoardAutomationControllerTests.cs`

## Target File Structure

Create:

- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
  - Owns lifecycle methods, per-frame tick, probe, purchase pass, purchase monitor, and snapshot creation.
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineState.cs`
  - Mutable engine-owned route execution state.
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineSnapshot.cs`
  - Immutable UI/diagnostics view of engine state.
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEnginePorts.cs`
  - MMF-local interfaces for route context, UI automation, market-board IO, purchase IO, reporting, evidence, and clock.
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineTickResult.cs`
  - Small result record for route/purchase tick outcomes.
- `src/MarketMafioso/MarketAcquisition/MarketAcquisitionClaimLifecycleController.cs`
  - Owns accepted-claim mutation and conflict reconciliation currently embedded in `MainWindow`.
- `src/MarketMafioso/MarketAcquisition/DalamudMarketAcquisitionRouteEngineAdapters.cs`
  - Concrete adapters that wrap current Dalamud/plugin services and unsafe UI operations.
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTestDoubles.cs`
  - Fake ports, in-memory reporter/evidence recorder, deterministic clock.
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineLifecycleTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTickTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineProbeTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEnginePurchaseTests.cs`
- `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineReportingTests.cs`
- `docs/superpowers/specs/2026-07-10-market-acquisition-route-engine-franthropy-candidates.md`

Modify:

- `src/MarketMafioso/Windows/MainWindow.cs`
  - Construct engine and concrete ports.
  - Delegate route lifecycle, probe, input capture, purchase monitor, and route status/snapshot reads.
  - Remove route execution fields and helper methods once migrated.
- `src/MarketMafioso/Windows/MarketAcquisitionPanels/MarketAcquisitionGuidedRoutePanel.cs`
  - Keep behavior unchanged. Update context access only when replacing `MainWindow` route-field delegates with engine snapshot delegates.
- `src/MarketMafioso/Windows/MarketAcquisitionPanels/MarketAcquisitionDiagnosticsPanel.cs`
  - Read engine snapshot values through delegates if `MainWindow` fields disappear.
- `src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
  - Read engine snapshot values through delegates if `MainWindow` fields disappear.

Do not modify:

- Server/dashboard API contracts.
- Route diagnostics CSV column names.
- Buy policy logic in `MarketAcquisitionLiveCandidatePlanner`.
- Purchase identity/revalidation logic in `MarketBoardPurchasePlanner`.
- Franthropy repositories or submodules.

---

### Task 1: Baseline Guardrails

**Files:**
- Inspect: `src/MarketMafioso/Windows/MainWindow.cs`
- Inspect: `docs/superpowers/specs/2026-07-10-market-acquisition-route-engine-extraction.md`

- [ ] **Step 1: Check branch and dirty state**

Run:

```powershell
git status -sb --untracked-files=all
git log --oneline -5
```

Expected: branch is `main`; note any pre-existing dirty work before editing. Do not stage unrelated files.

- [ ] **Step 2: Run baseline build**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 3: Run baseline focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteRunner|MarketBoardAutomationController|MarketBoardPurchaseSession|MarketBoardPurchaseExecutor|MarketBoardListingReadAccumulator|MarketAcquisitionLiveCandidatePlanner" --no-restore
```

Expected: focused tests pass.

- [ ] **Step 4: Record baseline `MainWindow` method map**

Run:

```powershell
rg -n "^    private (void|async|Task|IReadOnly|bool|string|Vector4|Market|Retainer|Automation|Craft)|^    public override|^    public .*" .\src\MarketMafioso\Windows\MainWindow.cs
(Get-Content .\src\MarketMafioso\Windows\MainWindow.cs).Count
```

Expected: output shows route conductor methods still present and gives the starting line count.

---

### Task 2: Add Engine Ports And Test Doubles

**Files:**
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEnginePorts.cs`
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineTickResult.cs`
- Create: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTestDoubles.cs`

- [ ] **Step 1: Write the initial failing compile test**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineLifecycleTests.cs` with this first test:

```csharp
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineLifecycleTests
{
    [Fact]
    public void Snapshot_DefaultsToIdleState()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();

        var snapshot = harness.Engine.CreateSnapshot();

        Assert.False(snapshot.IsRouteActive);
        Assert.False(snapshot.IsProbeRunning);
        Assert.Equal("No route has started.", snapshot.StatusMessage);
        Assert.Null(snapshot.MarketBoardReadResult);
        Assert.Null(snapshot.LiveCandidatePlan);
    }
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineLifecycleTests" --no-restore
```

Expected: compile fails because engine and harness types do not exist.

- [ ] **Step 2: Add port interfaces**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEnginePorts.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionRouteClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemMarketAcquisitionRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IMarketAcquisitionRouteContext
{
    bool IsCurrentWorldAvailable { get; }
    string GetCurrentWorldName();
    bool TryGetCharacterScope(out string characterName, out string homeWorld);
}

public interface IMarketAcquisitionRouteUiAutomation
{
    bool ProcessCommand(string command);
    bool TryCloseMarketBoardWindows();
    AutomationTravelPreflightResult CheckTravelPreflight();
    bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message);
}

public interface IMarketAcquisitionMarketBoardIo
{
    MarketBoardApproachResult OpenOrApproachMarketBoard();
    MarketBoardItemSearchResult SearchItem(uint itemId, string itemName);
    MarketBoardReadResult ReadCurrentListings(string currentWorld);
    MarketBoardInputCapture CaptureInputState();
}

public interface IMarketAcquisitionPurchaseIo
{
    MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead);

    MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate);
}

public interface IMarketAcquisitionRouteReporter
{
    bool CanReport { get; }
    Task ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken cancellationToken);
    Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken);
    Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken);
}

public interface IMarketAcquisitionRouteEvidenceRecorder
{
    void RecordProbeVisit(
        string currentWorld,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        string? requestId,
        string routeRunId);

    void RecordPurchaseVisit(
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string worldName,
        string? requestId,
        string routeRunId);
}
```

In the same file or adjacent records in the same namespace, add reporting DTOs:

```csharp
public sealed record MarketAcquisitionRouteProgressReport(
    string RequestId,
    string ClaimToken,
    string RouteState,
    string AttemptId,
    long Sequence,
    string? RouteStopId,
    string? ActiveWorld,
    string Phase,
    string Message);

public sealed record MarketAcquisitionPurchaseAuditReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string WorldName,
    uint ItemId,
    string? ItemName,
    MarketBoardPurchaseCandidate Candidate,
    string Message);

public sealed record MarketAcquisitionLineProgressReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string? ItemName,
    string Status,
    uint PurchasedQuantity,
    uint SpentGil,
    string Message,
    string? Reason);
```

- [ ] **Step 3: Add tick result record**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineTickResult.cs`:

```csharp
using System;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteEngineTickResult
{
    public bool DidWork { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset? NextTickUtc { get; init; }

    public static MarketAcquisitionRouteEngineTickResult Idle(string message = "") => new()
    {
        Message = message,
    };

    public static MarketAcquisitionRouteEngineTickResult Worked(
        string message,
        DateTimeOffset? nextTickUtc = null) => new()
        {
            DidWork = true,
            Message = message,
            NextTickUtc = nextTickUtc,
        };
}
```

- [ ] **Step 4: Add reusable test doubles**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTestDoubles.cs` with fake ports:

```csharp
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

internal sealed class FakeRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
}

internal sealed class FakeRouteContext : IMarketAcquisitionRouteContext
{
    public bool IsCurrentWorldAvailable { get; set; } = true;
    public string CurrentWorld { get; set; } = "Siren";
    public string CharacterName { get; set; } = "Tester";
    public string HomeWorld { get; set; } = "Siren";

    public string GetCurrentWorldName()
    {
        if (!IsCurrentWorldAvailable)
            throw new InvalidOperationException("Current world is unavailable.");

        return CurrentWorld;
    }

    public bool TryGetCharacterScope(out string characterName, out string homeWorld)
    {
        characterName = CharacterName;
        homeWorld = HomeWorld;
        return !string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(homeWorld);
    }
}

internal sealed class FakeRouteUiAutomation : IMarketAcquisitionRouteUiAutomation
{
    public List<string> Commands { get; } = [];
    public bool TravelPreflightCanSend { get; set; } = true;
    public bool CloseMarketBoardWindowsResult { get; set; }
    public bool ScrollListingsResult { get; set; } = true;
    public int? LastRequestedScrollRow { get; private set; }
    public string ScrollMessage { get; set; } = "Requested deeper listings.";

    public bool ProcessCommand(string command)
    {
        Commands.Add(command);
        return true;
    }

    public bool TryCloseMarketBoardWindows() => CloseMarketBoardWindowsResult;

    public AutomationTravelPreflightResult CheckTravelPreflight() =>
        new()
        {
            CanSendCommand = TravelPreflightCanSend,
            Message = TravelPreflightCanSend
                ? "No blocking UI is open."
                : "Close blocking UI before Lifestream travel: ItemSearch.",
            BlockingAddons = TravelPreflightCanSend ? [] : ["ItemSearch"],
        };

    public bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message)
    {
        LastRequestedScrollRow = requestedRow;
        message = ScrollMessage;
        return ScrollListingsResult;
    }
}
```

Continue that file with market-board, purchase, reporter, evidence, and harness fakes:

```csharp
internal sealed class FakeMarketBoardIo : IMarketAcquisitionMarketBoardIo
{
    public Queue<MarketBoardReadResult> Reads { get; } = [];
    public Queue<MarketBoardItemSearchResult> Searches { get; } = [];
    public MarketBoardApproachResult ApproachResult { get; set; } =
        MarketBoardApproachResult.Ready("Market board is ready.");

    public MarketBoardApproachResult OpenOrApproachMarketBoard() => ApproachResult;

    public MarketBoardItemSearchResult SearchItem(uint itemId, string itemName) =>
        Searches.Count == 0
            ? new MarketBoardItemSearchResult { Status = "ListingsReady", Message = "Listings are ready." }
            : Searches.Dequeue();

    public MarketBoardReadResult ReadCurrentListings(string currentWorld) =>
        Reads.Count == 0
            ? new MarketBoardReadResult { Status = "NoListings", Message = "No listings.", ReadState = MarketBoardListingReadState.FreshComplete, WorldName = currentWorld }
            : Reads.Dequeue();

    public MarketBoardInputCapture CaptureInputState() => new()
    {
        Status = "Captured",
        Message = "Captured input state.",
    };
}

internal sealed class FakePurchaseIo : IMarketAcquisitionPurchaseIo
{
    public Queue<MarketBoardPurchaseResult> PurchaseResults { get; } = [];
    public Queue<MarketBoardPurchaseResult> ConfirmationResults { get; } = [];

    public MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead) =>
        PurchaseResults.Count == 0
            ? new MarketBoardPurchaseResult { Status = "NoCandidate", Message = "No safe candidate." }
            : PurchaseResults.Dequeue();

    public MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate) =>
        ConfirmationResults.Count == 0
            ? new MarketBoardPurchaseResult { Status = "ConfirmationPending", Message = "Waiting.", Candidate = candidate }
            : ConfirmationResults.Dequeue();
}

internal sealed class RecordingRouteReporter : IMarketAcquisitionRouteReporter
{
    public bool CanReport { get; set; } = true;
    public List<MarketAcquisitionRouteProgressReport> RouteProgressReports { get; } = [];
    public List<MarketAcquisitionPurchaseAuditReport> PurchaseAuditReports { get; } = [];
    public List<MarketAcquisitionLineProgressReport> LineProgressReports { get; } = [];

    public Task ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken cancellationToken)
    {
        RouteProgressReports.Add(report);
        return Task.CompletedTask;
    }

    public Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken)
    {
        PurchaseAuditReports.Add(report);
        return Task.CompletedTask;
    }

    public Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken)
    {
        LineProgressReports.Add(report);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRouteEvidenceRecorder : IMarketAcquisitionRouteEvidenceRecorder
{
    public int ProbeVisits { get; private set; }
    public int PurchaseVisits { get; private set; }

    public void RecordProbeVisit(
        string currentWorld,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        string? requestId,
        string routeRunId) => ProbeVisits++;

    public void RecordPurchaseVisit(
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string worldName,
        string? requestId,
        string routeRunId) => PurchaseVisits++;
}
```

The harness will compile after Task 3 adds the engine constructor. Add it at the bottom:

```csharp
internal sealed class MarketAcquisitionRouteEngineHarness : IDisposable
{
    public FakeRouteClock Clock { get; } = new();
    public FakeRouteContext Context { get; } = new();
    public FakeRouteUiAutomation Ui { get; } = new();
    public FakeMarketBoardIo MarketBoard { get; } = new();
    public FakePurchaseIo Purchase { get; } = new();
    public RecordingRouteReporter Reporter { get; } = new();
    public RecordingRouteEvidenceRecorder Evidence { get; } = new();
    public MarketAcquisitionRouteRunner Runner { get; }
    public MarketAcquisitionRouteEngine Engine { get; }

    private MarketAcquisitionRouteEngineHarness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoRouteEngineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Runner = new MarketAcquisitionRouteRunner(directory);
        Engine = new MarketAcquisitionRouteEngine(
            Runner,
            Context,
            Ui,
            MarketBoard,
            Purchase,
            Reporter,
            Evidence,
            Clock);
    }

    public static MarketAcquisitionRouteEngineHarness Create() => new();

    public void Dispose() => Runner.Dispose();
}
```

- [ ] **Step 5: Compile to verify expected missing engine only**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineLifecycleTests" --no-restore
```

Expected: compile fails only on `MarketAcquisitionRouteEngine` and related state/snapshot types. Fix any typo in fake code before moving on.

---

### Task 3: Add Engine State, Snapshot, And Minimal Engine Shell

**Files:**
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineState.cs`
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineSnapshot.cs`
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineLifecycleTests.cs`

- [ ] **Step 1: Add engine state**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineState.cs`:

```csharp
using System;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionRouteEngineState
{
    public MarketBoardReadResult? MarketBoardReadResult { get; set; }
    public MarketBoardListingReconciliation? MarketBoardReconciliation { get; set; }
    public MarketAcquisitionLiveCandidatePlan? LiveCandidatePlan { get; set; }
    public uint ActiveWorldPurchasedQuantity { get; set; }
    public uint ActiveWorldSpentGil { get; set; }
    public string? ActiveWorldPurchaseBatchWorld { get; set; }
    public string? ActivePurchaseLineId { get; set; }
    public uint ActiveLinePurchasedQuantity { get; set; }
    public uint ActiveLineSpentGil { get; set; }
    public bool ProbeRunning { get; set; }
    public DateTimeOffset NextRouteMonitorUtc { get; set; } = DateTimeOffset.MinValue;
    public long ProgressReportSequence { get; set; }
    public long ProgressSessionVersion { get; set; }
    public string ProgressNonce { get; set; } = Guid.NewGuid().ToString("N");
    public string? LastProgressReportKey { get; set; }
    public string AcquisitionStatus { get; set; } = "No route has started.";

    public void ResetRouteExecutionState()
    {
        MarketBoardReadResult = null;
        MarketBoardReconciliation = null;
        LiveCandidatePlan = null;
        ActiveWorldPurchasedQuantity = 0;
        ActiveWorldSpentGil = 0;
        ActiveWorldPurchaseBatchWorld = null;
        ActivePurchaseLineId = null;
        ActiveLinePurchasedQuantity = 0;
        ActiveLineSpentGil = 0;
        ProbeRunning = false;
        NextRouteMonitorUtc = DateTimeOffset.MinValue;
        ProgressSessionVersion++;
        ProgressReportSequence = 0;
        ProgressNonce = Guid.NewGuid().ToString("N");
        LastProgressReportKey = null;
    }
}
```

- [ ] **Step 2: Add snapshot record**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngineSnapshot.cs`:

```csharp
namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteEngineSnapshot
{
    public string StatusMessage { get; init; } = string.Empty;
    public string VisibleAcquisitionStatus { get; init; } = string.Empty;
    public bool IsRouteActive { get; init; }
    public bool IsProbeRunning { get; init; }
    public MarketBoardReadResult? MarketBoardReadResult { get; init; }
    public MarketBoardListingReconciliation? MarketBoardReconciliation { get; init; }
    public MarketAcquisitionLiveCandidatePlan? LiveCandidatePlan { get; init; }
    public MarketBoardPurchaseSession? PurchaseSession { get; init; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; init; }
    public uint ActiveWorldPurchasedQuantity { get; init; }
    public uint ActiveWorldSpentGil { get; init; }
    public uint ActiveLinePurchasedQuantity { get; init; }
    public uint ActiveLineSpentGil { get; init; }
    public string? LastDiagnosticFilePath { get; init; }
    public string? LastObservedListingsCsvPath { get; init; }
    public string? LastPurchaseRecordsCsvPath { get; init; }
    public MarketAcquisitionRouteRunSummary? LastRunSummary { get; init; }
    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; init; }
}
```

- [ ] **Step 3: Add minimal engine shell**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`:

```csharp
using System;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine
{
    private readonly MarketAcquisitionRouteRunner runner;
    private readonly IMarketAcquisitionRouteContext context;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly IMarketAcquisitionMarketBoardIo marketBoard;
    private readonly IMarketAcquisitionPurchaseIo purchase;
    private readonly IMarketAcquisitionRouteReporter reporter;
    private readonly IMarketAcquisitionRouteEvidenceRecorder evidence;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteEngineState state = new();

    public MarketAcquisitionRouteEngine(
        MarketAcquisitionRouteRunner runner,
        IMarketAcquisitionRouteContext context,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        IMarketAcquisitionMarketBoardIo marketBoard,
        IMarketAcquisitionPurchaseIo purchase,
        IMarketAcquisitionRouteReporter reporter,
        IMarketAcquisitionRouteEvidenceRecorder evidence,
        IMarketAcquisitionRouteClock clock)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public MarketAcquisitionRouteEngineSnapshot CreateSnapshot() => new()
    {
        StatusMessage = runner.StatusMessage,
        VisibleAcquisitionStatus = state.AcquisitionStatus,
        IsRouteActive = IsRouteActive,
        IsProbeRunning = state.ProbeRunning,
        MarketBoardReadResult = state.MarketBoardReadResult,
        MarketBoardReconciliation = state.MarketBoardReconciliation,
        LiveCandidatePlan = state.LiveCandidatePlan,
        PurchaseSession = purchaseAutomation.PurchaseSession,
        LastPurchaseResult = purchaseAutomation.LastPurchaseResult,
        ActiveWorldPurchasedQuantity = state.ActiveWorldPurchasedQuantity,
        ActiveWorldSpentGil = state.ActiveWorldSpentGil,
        ActiveLinePurchasedQuantity = state.ActiveLinePurchasedQuantity,
        ActiveLineSpentGil = state.ActiveLineSpentGil,
        LastDiagnosticFilePath = runner.LastDiagnosticFilePath,
        LastObservedListingsCsvPath = runner.LastObservedListingsCsvPath,
        LastPurchaseRecordsCsvPath = runner.LastPurchaseRecordsCsvPath,
        LastRunSummary = runner.LastRunSummary,
        LatestWorldCompletionSummary = runner.LatestWorldCompletionSummary,
    };

    public bool IsRouteActive =>
        runner.IsRunning ||
        runner.IsPaused ||
        state.ProbeRunning ||
        purchaseAutomation.PurchaseSession?.IsActive == true;
}
```

- [ ] **Step 4: Run the first test**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineLifecycleTests" --no-restore
```

Expected: the `Snapshot_DefaultsToIdleState` test passes.

- [ ] **Step 5: Commit shell**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine*.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngine*.cs
git commit -m "refactor: add acquisition route engine shell"
```

---

### Task 4: Move Route Lifecycle Commands

**Files:**
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineLifecycleTests.cs`

- [ ] **Step 1: Add failing lifecycle tests**

Append tests for start/reset/pause/resume/stop:

```csharp
[Fact]
public void Start_BeginsRunnerAndResetsExecutionState()
{
    using var harness = MarketAcquisitionRouteEngineHarness.Create();
    var plan = MarketAcquisitionRouteEngineTestData.Plan("Maduin");
    var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();

    var result = harness.Engine.Start(plan, claim, enableDiagnostics: false, includeOpportunisticChecks: true);

    Assert.True(result.Success);
    Assert.Equal("Running", harness.Runner.State);
    Assert.True(harness.Engine.CreateSnapshot().IsRouteActive);
    Assert.Equal(0u, harness.Engine.CreateSnapshot().ActiveWorldPurchasedQuantity);
}

[Fact]
public void Reset_StopsAutomationAndClearsSnapshotState()
{
    using var harness = MarketAcquisitionRouteEngineHarness.Create();
    harness.Engine.Reset("No route has started.");

    var snapshot = harness.Engine.CreateSnapshot();

    Assert.False(snapshot.IsRouteActive);
    Assert.Equal("No route has started.", snapshot.VisibleAcquisitionStatus);
    Assert.Null(snapshot.MarketBoardReadResult);
}
```

Use the `MarketAcquisitionRouteEngineTestData` helper created in Task 2 for all new engine tests. Add plan variants there as each test needs them so the engine suite does not depend on route-runner test fixtures.

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineLifecycleTests" --no-restore
```

Expected: compile fails for missing lifecycle methods.

- [ ] **Step 2: Implement lifecycle methods**

Add to `MarketAcquisitionRouteEngine`:

```csharp
private MarketAcquisitionClaimView? claimedRequest;

public MarketAcquisitionRouteActionResult Start(
    MarketAcquisitionPlan plan,
    MarketAcquisitionClaimView claimed,
    bool enableDiagnostics,
    bool includeOpportunisticChecks)
{
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(claimed);

    claimedRequest = claimed;
    state.ResetRouteExecutionState();
    listingReadAccumulator.Clear();
    purchaseAutomation.Clear();
    var result = runner.Start(plan, enableDiagnostics, includeOpportunisticChecks);
    state.AcquisitionStatus = result.Message;
    return result;
}

public MarketAcquisitionRouteActionResult Pause()
{
    var result = runner.Pause();
    state.AcquisitionStatus = result.Message;
    return result;
}

public MarketAcquisitionRouteActionResult Resume()
{
    var result = runner.Resume();
    state.AcquisitionStatus = result.Message;
    return result;
}

public MarketAcquisitionRouteActionResult Stop()
{
    var result = runner.Stop();
    listingReadAccumulator.Clear();
    purchaseAutomation.Clear();
    state.AcquisitionStatus = result.Message;
    return result;
}

public void Reset(string status)
{
    runner.Reset(status);
    listingReadAccumulator.Clear();
    purchaseAutomation.Clear();
    state.ResetRouteExecutionState();
    state.AcquisitionStatus = status;
    claimedRequest = null;
}
```

Add restart/reprepare only after the same test pattern exists:

```csharp
public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
{
    ArgumentNullException.ThrowIfNull(plan);
    state.ResetRouteExecutionState();
    listingReadAccumulator.Clear();
    purchaseAutomation.Clear();
    var result = runner.Restart(plan);
    state.AcquisitionStatus = result.Message;
    return result;
}

public MarketAcquisitionRouteActionResult ReprepareAndRestart(MarketAcquisitionPlan plan, DateTimeOffset preparedAtUtc)
{
    ArgumentNullException.ThrowIfNull(plan);
    state.ResetRouteExecutionState();
    listingReadAccumulator.Clear();
    purchaseAutomation.Clear();
    var result = runner.ReprepareAndRestart(plan, preparedAtUtc);
    state.AcquisitionStatus = result.Message;
    return result;
}
```

- [ ] **Step 3: Run lifecycle tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineLifecycleTests" --no-restore
```

Expected: lifecycle tests pass.

- [ ] **Step 4: Commit lifecycle extraction**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineLifecycleTests.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTestDoubles.cs
git commit -m "refactor: move route lifecycle into engine"
```

---

### Task 5: Move Per-Frame Route Tick

**Files:**
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTickTests.cs`

- [ ] **Step 1: Add route tick tests**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineTickTests.cs`:

```csharp
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineTickTests
{
    [Fact]
    public void Tick_PendingStopOnDifferentWorldSendsTravelCommandWhenPreflightPasses()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Zalera";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Equal(["/li Maduin mb"], harness.Ui.Commands);
        Assert.Equal("TravelCommandSent", harness.Runner.ActiveStop?.Status);
    }

    [Fact]
    public void Tick_PendingStopBlockedByUiDoesNotSendTravelCommand()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Zalera";
        harness.Ui.TravelPreflightCanSend = false;
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.Empty(harness.Ui.Commands);
        Assert.Equal("Pending", harness.Runner.ActiveStop?.Status);
        Assert.Contains("blocked", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tick_ArrivedStopSearchesActiveItemWhenMarketBoardReady()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Searches.Enqueue(new MarketBoardItemSearchResult
        {
            Status = "ListingsReady",
            Message = "Listings ready.",
        });

        harness.Engine.TickRoute(isRequestBusy: false);

        Assert.True(harness.Runner.SearchSubmitted);
        Assert.True(harness.Engine.CreateSnapshot().IsProbeRunning);
    }
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineTickTests" --no-restore
```

Expected: compile fails for missing `TickRoute`.

- [ ] **Step 2: Implement `TickRoute`**

Move the logic from `MainWindow.MonitorGuidedRoute()` into `MarketAcquisitionRouteEngine.TickRoute(bool isRequestBusy)`.

Add method skeleton:

```csharp
public MarketAcquisitionRouteEngineTickResult TickRoute(bool isRequestBusy)
{
    if (isRequestBusy || state.ProbeRunning)
        return MarketAcquisitionRouteEngineTickResult.Idle();

    if (!runner.IsRunning)
        return MarketAcquisitionRouteEngineTickResult.Idle();

    var now = clock.UtcNow;
    if (now < state.NextRouteMonitorUtc)
        return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for next route monitor tick.");

    state.NextRouteMonitorUtc = now.AddMilliseconds(500);

    try
    {
        var activeStop = runner.ActiveStop;
        if (activeStop == null)
            return MarketAcquisitionRouteEngineTickResult.Idle("Route has no active stop.");

        if (string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            HandlePendingStop(activeStop);
        else if (!context.IsCurrentWorldAvailable)
            runner.RecordCurrentWorldUnavailable();
        else
            HandleWorldScopedStop(activeStop, context.GetCurrentWorldName());

        if (runner.ActiveStop is { Status: "Purchasing" } &&
            purchaseAutomation.PurchaseSession?.IsActive != true)
            BeginNextWorldPurchase();

        ReportRouteProgress();
        return MarketAcquisitionRouteEngineTickResult.Worked(runner.StatusMessage, state.NextRouteMonitorUtc);
    }
    catch (Exception ex)
    {
        runner.FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
        state.AcquisitionStatus = runner.StatusMessage;
        ReportRouteProgress();
        return MarketAcquisitionRouteEngineTickResult.Worked(runner.StatusMessage, state.NextRouteMonitorUtc);
    }
}
```

Implement the private helpers above with the existing branch bodies from `MainWindow.MonitorGuidedRoute()`:

- `context.IsCurrentWorldAvailable`
- `context.GetCurrentWorldName()`
- `uiAutomation.TryCloseMarketBoardWindows()`
- `uiAutomation.CheckTravelPreflight()`
- `uiAutomation.ProcessCommand(...)`
- `marketBoard.OpenOrApproachMarketBoard()`
- `marketBoard.SearchItem(...)`
- `runner.BeginProbe(...)` followed by a private `BeginProbe()` helper that sets `state.ProbeRunning = true`

Required helper shape:

```csharp
private void HandlePendingStop(MarketAcquisitionGuidedRouteStop activeStop)
{
    if (runner.MarketBoardCloseRequiredBeforeTravel)
    {
        if (uiAutomation.TryCloseMarketBoardWindows())
        {
            state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
            return;
        }

        runner.RecordMarketBoardClosedBeforeTravel();
        state.AcquisitionStatus = runner.StatusMessage;
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
        return;
    }

    var currentWorld = context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : null;
    if (context.IsCurrentWorldAvailable &&
        !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) &&
        !EnsureRouteTravelUiIsClear())
    {
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
        return;
    }

    runner.PreparePendingStopForCurrentWorld(context.IsCurrentWorldAvailable, currentWorld, uiAutomation.ProcessCommand);
    state.AcquisitionStatus = runner.StatusMessage;
    state.NextRouteMonitorUtc = clock.UtcNow.AddSeconds(2);
}

private bool EnsureRouteTravelUiIsClear()
{
    var preflight = uiAutomation.CheckTravelPreflight();
    if (!preflight.CanSendCommand)
    {
        runner.RecordTravelBlockedByUi(preflight);
        state.AcquisitionStatus = preflight.Message;
        return false;
    }

    return true;
}

private void HandleWorldScopedStop(MarketAcquisitionGuidedRouteStop activeStop, string currentWorld)
{
    if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
    {
        runner.RecordCurrentWorld(currentWorld);
        state.AcquisitionStatus = runner.StatusMessage;
        return;
    }

    if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
        runner.RecordCurrentWorld(currentWorld);

    if (runner.ActiveStop?.Status == "Arrived")
        HandleArrivedStop(currentWorld);
}

private void HandleArrivedStop(string currentWorld)
{
    var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
    if (runner.SearchSubmitted)
    {
        BeginProbeForCurrentLine(currentWorld, claimed);
        return;
    }

    var approachResult = marketBoard.OpenOrApproachMarketBoard();
    runner.RecordMarketBoardApproach(approachResult);
    state.AcquisitionStatus = approachResult.Message;
    if (approachResult.MarketBoardTravelNeeded)
    {
        if (!EnsureRouteTravelUiIsClear())
        {
            state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
            return;
        }

        runner.ExecuteMarketBoardTravelCommand(uiAutomation.ProcessCommand);
        state.AcquisitionStatus = runner.StatusMessage;
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(750);
        return;
    }

    if (!approachResult.ReadyToSearch)
    {
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
        return;
    }

    var activeLine = GetActiveRouteLine(claimed);
    var searchResult = marketBoard.SearchItem(activeLine.ItemId, activeLine.ItemName);
    runner.RecordSearchResult(searchResult, clock.UtcNow);
    state.AcquisitionStatus = searchResult.Message;
    if (!searchResult.ReadyForListings)
    {
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
        return;
    }

    state.NextRouteMonitorUtc = clock.UtcNow;
    BeginProbeForCurrentLine(currentWorld, claimed);
}

private void BeginProbeForCurrentLine(string currentWorld, MarketAcquisitionClaimView claimed)
{
    var activeLine = GetActiveRouteLine(claimed);
    runner.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {activeLine.ItemName ?? activeLine.ItemId.ToString(CultureInfo.InvariantCulture)}.");
    state.ProbeRunning = true;
    ProbeLiveMarketBoard();
}
```

Add a temporary private no-op `ReportRouteProgress()` that will be replaced in Task 9:

```csharp
private void ReportRouteProgress()
{
}
```

- [ ] **Step 3: Run tick tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineTickTests" --no-restore
```

Expected: route tick tests pass.

- [ ] **Step 4: Commit route tick move**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTickTests.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTestDoubles.cs
git commit -m "refactor: move route tick into engine"
```

---

### Task 6: Move Live Listing Probe And Continuation

**Files:**
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineProbeTests.cs`

- [ ] **Step 1: Add probe tests**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineProbeTests.cs`:

```csharp
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineProbeTests
{
    [Fact]
    public void Probe_StaleReadRecordsPendingAndDoesNotBuildCandidatePlan()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "ListingCacheSwitching",
            Message = "Switching item.",
            ReadState = MarketBoardListingReadState.SwitchingItem,
            ItemId = 7017,
            WorldName = "Maduin",
        });

        harness.Engine.ProbeLiveMarketBoard();

        var snapshot = harness.Engine.CreateSnapshot();
        Assert.NotNull(snapshot.MarketBoardReadResult);
        Assert.Null(snapshot.LiveCandidatePlan);
        Assert.Equal("Arrived", harness.Runner.ActiveStop?.Status);
    }

    [Fact]
    public void Probe_FreshPartialIncompleteCoverageRequestsDeeperListingRow()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.PartialReadWithoutSafeRows("Maduin"));

        harness.Engine.ProbeLiveMarketBoard();

        Assert.NotNull(harness.Ui.LastRequestedScrollRow);
        Assert.Equal("Arrived", harness.Runner.ActiveStop?.Status);
        Assert.Equal("IncompleteListingCoverage", harness.Engine.CreateSnapshot().LiveCandidatePlan?.Status);
    }
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineProbeTests" --no-restore
```

Expected: compile fails for missing `ProbeLiveMarketBoard`.

- [ ] **Step 2: Implement probe methods**

Move `ProbeLiveMarketBoardCore()` and `TryContinueVisibleListingRead(...)` into the engine as:

```csharp
public void ProbeLiveMarketBoard()
{
    var plan = runner.ActivePlan ?? throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings.");
    var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
    var activeLine = GetActiveRouteLine(claimed);
    var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
    var currentWorld = context.GetCurrentWorldName();

    state.MarketBoardReconciliation = null;
    state.LiveCandidatePlan = null;
    state.MarketBoardReadResult = listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));

    var canBuildLiveCandidatePlan = state.MarketBoardReadResult.Status is "Ready" or "NoListings";
    state.MarketBoardReconciliation = state.MarketBoardReadResult.Status == "Ready"
        ? activeSubtask == null
            ? MarketBoardListingReconciler.Reconcile(
                plan,
                currentWorld,
                state.MarketBoardReadResult.ItemId,
                state.MarketBoardReadResult.Listings)
            : MarketBoardListingReconciler.Reconcile(
                plan,
                activeSubtask,
                currentWorld,
                state.MarketBoardReadResult.ItemId,
                state.MarketBoardReadResult.Listings)
        : null;
    if (!state.MarketBoardReadResult.IsFresh)
    {
        if (runner.IsRunning)
            runner.RecordListingReadPending(currentWorld, state.MarketBoardReadResult);

        state.AcquisitionStatus = state.MarketBoardReadResult.Message;
        return;
    }

    var purchaseTotals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
    state.LiveCandidatePlan = canBuildLiveCandidatePlan
        ? activeSubtask == null
            ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                activeLine,
                plan,
                currentWorld,
                state.MarketBoardReadResult,
                purchaseTotals.PurchasedQuantity,
                purchaseTotals.SpentGil)
            : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                activeLine,
                plan,
                activeSubtask,
                currentWorld,
                state.MarketBoardReadResult,
                purchaseTotals.PurchasedQuantity,
                purchaseTotals.SpentGil)
        : null;

    if (state.LiveCandidatePlan != null &&
        TryContinueVisibleListingRead(currentWorld, state.MarketBoardReadResult, state.LiveCandidatePlan))
        return;

    var guidedRouteResult = runner.IsRunning &&
                            runner.ActiveStop is { Status: "Arrived" } &&
                            state.LiveCandidatePlan != null
        ? runner.RecordProbe(currentWorld, state.LiveCandidatePlan)
        : null;
    if (guidedRouteResult?.Success == true && state.LiveCandidatePlan != null)
        evidence.RecordProbeVisit(currentWorld, activeLine, activeSubtask, state.LiveCandidatePlan);

    state.AcquisitionStatus = state.MarketBoardReconciliation == null
        ? state.MarketBoardReadResult.Message
        : $"Live listing reconciliation {state.MarketBoardReconciliation.Status}; live candidates {state.LiveCandidatePlan?.Status ?? "Unavailable"}.";
    if (guidedRouteResult != null)
        state.AcquisitionStatus = $"{state.AcquisitionStatus} Route: {guidedRouteResult.Message}";

    state.ProbeRunning = false;
    state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
}
```

Move these helper methods from `MainWindow` into `MarketAcquisitionRouteEngine`:

- `GetActiveRouteLine(...)`
- `GetActiveRouteLineId(...)`
- `ResolveActiveRouteLinePurchaseTotals(...)`
- `ShouldFailWorldPurchaseBatchOnNoCandidate(...)`
- `ResolveZeroPurchaseLineStatus(...)`

Keep formatting helpers in `MainWindow`; engine messages can use plain item names/ids.

Implement continuation using:

```csharp
private bool TryContinueVisibleListingRead(
    string currentWorld,
    MarketBoardReadResult readResult,
    MarketAcquisitionLiveCandidatePlan candidatePlan)
{
    if (!runner.IsRunning ||
        !listingReadAccumulator.TryBeginContinuation(readResult, candidatePlan, out var continuation))
        return false;

    if (!uiAutomation.TryScrollMarketBoardListingsToRow(continuation.RequestedRow, out var scrollMessage))
    {
        state.AcquisitionStatus = scrollMessage;
        runner.RecordListingReadPending(currentWorld, readResult with { Message = $"{continuation.Message} {scrollMessage}" });
        return false;
    }

    var message = $"{continuation.Message} {scrollMessage}";
    state.AcquisitionStatus = message;
    var pending = runner.RecordListingReadPending(currentWorld, readResult with { Message = message });
    if (!pending.Success)
        state.AcquisitionStatus = pending.Message;

    state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(500);
    return true;
}
```

- [ ] **Step 3: Run probe tests and existing coverage tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineProbeTests|MarketBoardListingReadAccumulator|MarketAcquisitionLiveCandidatePlanner|MarketAcquisitionRouteRunner" --no-restore
```

Expected: tests pass.

- [ ] **Step 4: Commit probe extraction**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineProbeTests.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTestDoubles.cs
git commit -m "refactor: move route probing into engine"
```

---

### Task 7: Move Purchase Pass

**Files:**
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEnginePurchaseTests.cs`

- [ ] **Step 1: Add purchase pass tests**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEnginePurchaseTests.cs`:

```csharp
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEnginePurchaseTests
{
    [Fact]
    public void BeginNextWorldPurchase_NoCandidateWithIncompleteCoverageFailsRoute()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.PartialReadWithoutSafeRows("Maduin"));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult { Status = "NoCandidate", Message = "No safe candidate." });

        harness.Engine.BeginNextWorldPurchase();

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Contains("No visible live listings", harness.Runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BeginNextWorldPurchase_SelectionSentStartsPurchaseSession()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Context.CurrentWorld = "Maduin";
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
        harness.Runner.RecordCurrentWorld("Maduin");
        harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
        harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Message = "Selection sent.",
            Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
        });

        harness.Engine.BeginNextWorldPurchase();

        Assert.Equal("WaitingForConfirmation", harness.Engine.CreateSnapshot().PurchaseSession?.Status);
    }
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEnginePurchaseTests" --no-restore
```

Expected: compile fails for missing `BeginNextWorldPurchase`.

- [ ] **Step 2: Implement purchase pass**

Move `BeginNextWorldPurchase()`, `CompleteActiveWorldPurchaseBatch(...)`, `ResetMarketBoardStateForNextRouteItem(...)`, and `ClearMarketBoardAutomationState()` from `MainWindow` into the engine.

Keep the constants near the engine:

```csharp
private static readonly TimeSpan MarketBoardPurchaseConfirmationWatchdog = TimeSpan.FromSeconds(15);
private static readonly TimeSpan MarketBoardPurchaseInitialMonitorDelay = TimeSpan.FromMilliseconds(250);
```

Use `purchase.ExecuteFirstCandidate(...)` instead of `marketBoardPurchaseExecutor.ExecuteFirstCandidate(...)`.

Replace `nextGuidedRouteMonitorUtc` with `state.NextRouteMonitorUtc`.

Replace `marketBoardReadResult`, `marketBoardReconciliation`, and `marketAcquisitionLiveCandidatePlan` with `state.*`.

Record purchase selection snapshots through `runner.RecordAutomationSnapshot(CreatePurchaseSelectionSnapshot(purchaseResult))`; move snapshot helper methods into the engine.

- [ ] **Step 3: Run purchase pass tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEnginePurchaseTests|MarketBoardPurchaseExecutor|MarketBoardPurchasePlanner|MarketAcquisitionClaimStatus" --no-restore
```

Expected: tests pass.

- [ ] **Step 4: Commit purchase pass extraction**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEnginePurchaseTests.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTestDoubles.cs
git commit -m "refactor: move purchase pass into route engine"
```

---

### Task 8: Move Purchase Monitor

**Files:**
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEnginePurchaseTests.cs`

- [ ] **Step 1: Add purchase monitor tests**

Append tests:

```csharp
[Fact]
public void MonitorMarketBoardPurchase_CompletedPurchaseIncrementsCounters()
{
    using var harness = MarketAcquisitionRouteEngineHarness.Create();
    harness.Context.CurrentWorld = "Maduin";
    harness.Engine.Start(MarketAcquisitionRouteEngineTestData.MultiLinePlan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);
    harness.Runner.RecordCurrentWorld("Maduin");
    harness.Runner.RecordProbe("Maduin", MarketAcquisitionRouteEngineTestData.ReadyCandidatePlan());
    harness.MarketBoard.Reads.Enqueue(MarketAcquisitionRouteEngineTestData.ReadWithSafeListing("Maduin"));
    harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
    {
        Status = "PurchaseSelectionSent",
        Message = "Selection sent.",
        Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
    });
    harness.Purchase.ConfirmationResults.Enqueue(new MarketBoardPurchaseResult
    {
        Status = "ConfirmationSubmitted",
        Message = "Submitted.",
        Candidate = MarketAcquisitionRouteEngineTestData.Candidate("Maduin"),
    });
    harness.Engine.BeginNextWorldPurchase();
    harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
    harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
    {
        Status = "NoListings",
        Message = "No listings remain.",
        ReadState = MarketBoardListingReadState.FreshComplete,
        WorldName = "Maduin",
    });

    harness.Engine.MonitorMarketBoardPurchase(isRequestBusy: false);

    var snapshot = harness.Engine.CreateSnapshot();
    Assert.Equal(4u, snapshot.ActiveWorldPurchasedQuantity);
    Assert.Equal(3200u, snapshot.ActiveWorldSpentGil);
    Assert.Single(harness.Reporter.PurchaseAuditReports);
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEnginePurchaseTests" --no-restore
```

Expected: compile fails for missing `MonitorMarketBoardPurchase`.

- [ ] **Step 2: Implement purchase monitor**

Move `MonitorMarketBoardPurchase()`, `ReportConfirmedPurchase(...)`, `ReportAcquisitionLineProgress(...)`, `CreatePurchaseConfirmationSnapshot(...)`, `ClassifyPurchaseConfirmationOutcome(...)`, and related helpers into the engine.

Add:

```csharp
private static readonly TimeSpan MarketBoardPurchaseListingRemovalWatchdog = TimeSpan.FromSeconds(15);
private static readonly TimeSpan MarketBoardPurchaseMonitorInterval = TimeSpan.FromMilliseconds(500);
```

Public method:

```csharp
public MarketAcquisitionRouteEngineTickResult MonitorMarketBoardPurchase(bool isRequestBusy)
{
    if (isRequestBusy)
        return MarketAcquisitionRouteEngineTickResult.Idle();

    var session = purchaseAutomation.PurchaseSession;
    if (session?.IsActive != true)
        return MarketAcquisitionRouteEngineTickResult.Idle();

    var now = clock.UtcNow;
    if (!purchaseAutomation.IsMonitorDue(now))
        return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for purchase monitor tick.");

    ProcessPurchaseMonitorSession(session, now);
    return MarketAcquisitionRouteEngineTickResult.Worked(runner.StatusMessage, purchaseAutomation.NextMonitorUtc);
}
```

Use `purchase.TryConfirmPendingPurchase(...)` and `marketBoard.ReadCurrentListings(context.GetCurrentWorldName())`.

Implement `ProcessPurchaseMonitorSession` by moving the existing confirmation branches in this order:

```csharp
private void ProcessPurchaseMonitorSession(MarketBoardPurchaseSession session, DateTimeOffset now)
{
    if (session.PendingCandidate == null)
    {
        purchaseAutomation.CompleteSession("No pending candidate remains.");
        return;
    }

    var confirmation = purchase.TryConfirmPendingPurchase(session.PendingCandidate);
    purchaseAutomation.RecordPurchaseResult(confirmation, now);
    if (confirmation.Status is "ConfirmationSubmitted")
    {
        purchaseAutomation.ScheduleNextMonitor(now, MarketBoardPurchaseMonitorInterval);
        return;
    }

    if (confirmation.Status is "Purchased")
    {
        ReportConfirmedPurchase(confirmation);
        CompleteActiveWorldPurchaseBatch(context.GetCurrentWorldName());
        return;
    }

    if (confirmation.Status is "ListingStillVisible")
    {
        var freshRead = marketBoard.ReadCurrentListings(context.GetCurrentWorldName());
        var snapshot = CreatePurchaseConfirmationSnapshot(session.PendingCandidate, freshRead);
        var outcome = ClassifyPurchaseConfirmationOutcome(session.PendingCandidate, snapshot, now);
        ApplyPurchaseConfirmationOutcome(outcome, freshRead);
        return;
    }

    if (!session.IsActive)
    {
        runner.FailRoute($"World purchase batch stopped: {session.Message}");
        state.AcquisitionStatus = runner.StatusMessage;
        ReportRouteProgress();
    }
}
```

- [ ] **Step 3: Run purchase monitor tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEnginePurchaseTests|MarketBoardAutomationController|MarketBoardPurchaseSession" --no-restore
```

Expected: tests pass.

- [ ] **Step 4: Commit monitor extraction**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEnginePurchaseTests.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineTestDoubles.cs
git commit -m "refactor: move purchase monitor into route engine"
```

---

### Task 9: Move Reporting And Claim Conflict Reconciliation

**Files:**
- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionClaimLifecycleController.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEngine.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteEnginePorts.cs`
- Create: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineReportingTests.cs`

- [ ] **Step 1: Add reporting tests**

Create `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteEngineReportingTests.cs`:

```csharp
namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineReportingTests
{
    [Fact]
    public void ReportRouteProgress_CoalescesDuplicateMessages()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.ReportRouteProgress();
        harness.Engine.ReportRouteProgress();

        Assert.Single(harness.Reporter.RouteProgressReports);
    }

    [Fact]
    public void ReportRouteProgress_SkipsWhenReporterCannotReport()
    {
        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Reporter.CanReport = false;
        harness.Engine.Start(MarketAcquisitionRouteEngineTestData.Plan("Maduin"), MarketAcquisitionRouteEngineTestData.AcceptedClaim(), false, false);

        harness.Engine.ReportRouteProgress();

        Assert.Empty(harness.Reporter.RouteProgressReports);
    }
}
```

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineReportingTests" --no-restore
```

Expected: compile fails if `ReportRouteProgress` is still private/no-op.

- [ ] **Step 2: Implement reporting**

Move route progress idempotency from `MainWindow.ReportGuidedRouteProgress()` into engine `ReportRouteProgress()`.

Use the reporter port:

```csharp
public void ReportRouteProgress()
{
    var claimed = claimedRequest;
    if (claimed == null ||
        string.IsNullOrWhiteSpace(claimed.ClaimToken) ||
        !reporter.CanReport ||
        string.Equals(runner.State, "Idle", StringComparison.OrdinalIgnoreCase))
        return;

    var runnerState = runner.State;
    if (!MarketAcquisitionRouteProgressReporter.CanReportForRouteState(runnerState) ||
        !MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
        return;

    var message = runner.StatusMessage;
    var reportKey = $"{claimed.Id}|{runnerState}|{message}";
    if (string.Equals(state.LastProgressReportKey, reportKey, StringComparison.Ordinal))
        return;

    state.LastProgressReportKey = reportKey;
    var sequence = ++state.ProgressReportSequence;
    var activeStop = runner.ActiveStop;
    var routeStopId = activeStop == null ? null : $"{activeStop.DataCenter}:{activeStop.WorldName}";
    var phase = activeStop?.Status ?? runnerState;

    _ = reporter.ReportRouteProgressAsync(
        new MarketAcquisitionRouteProgressReport(
            claimed.Id,
            claimed.ClaimToken,
            runnerState,
            state.ProgressNonce,
            sequence,
            routeStopId,
            activeStop?.WorldName,
            phase,
            message),
        CancellationToken.None);
}
```

For this slice, keep reporter async fire-and-forget behavior equivalent. If tests need synchronous assertions, the fake reporter completes synchronously.

- [ ] **Step 3: Add claim lifecycle controller**

Create `src/MarketMafioso/MarketAcquisition/MarketAcquisitionClaimLifecycleController.cs`:

```csharp
using System;
using System.Net;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionClaimLifecycleController
{
    private readonly Configuration config;
    private readonly Action<MarketAcquisitionClaimView?> setClaimedRequest;
    private readonly Func<MarketAcquisitionClaimView?> getClaimedRequest;
    private readonly Func<string?> getAcceptIdempotencyKey;
    private readonly Func<string?> getRejectIdempotencyKey;
    private readonly Action clearClaimMetadata;
    private readonly Action<string> setStatus;
    private readonly Func<string> getRouteStatusMessage;
    private readonly Action saveConfig;

    public MarketAcquisitionClaimLifecycleController(
        Configuration config,
        Func<MarketAcquisitionClaimView?> getClaimedRequest,
        Action<MarketAcquisitionClaimView?> setClaimedRequest,
        Func<string?> getAcceptIdempotencyKey,
        Func<string?> getRejectIdempotencyKey,
        Action clearClaimMetadata,
        Action<string> setStatus,
        Func<string> getRouteStatusMessage,
        Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.getClaimedRequest = getClaimedRequest ?? throw new ArgumentNullException(nameof(getClaimedRequest));
        this.setClaimedRequest = setClaimedRequest ?? throw new ArgumentNullException(nameof(setClaimedRequest));
        this.getAcceptIdempotencyKey = getAcceptIdempotencyKey ?? throw new ArgumentNullException(nameof(getAcceptIdempotencyKey));
        this.getRejectIdempotencyKey = getRejectIdempotencyKey ?? throw new ArgumentNullException(nameof(getRejectIdempotencyKey));
        this.clearClaimMetadata = clearClaimMetadata ?? throw new ArgumentNullException(nameof(clearClaimMetadata));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.getRouteStatusMessage = getRouteStatusMessage ?? throw new ArgumentNullException(nameof(getRouteStatusMessage));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public bool TryHandleRouteProgressConflict(Exception exception, MarketAcquisitionClaimView claimed, long reportSessionVersion, long currentSessionVersion)
    {
        if (exception is not MarketAcquisitionLifecycleHttpException { StatusCode: System.Net.HttpStatusCode.Conflict } conflict ||
            !TryExtractInvalidTransitionSourceStatus(conflict.Error, out var sourceStatus))
            return false;

        if (currentSessionVersion != reportSessionVersion ||
            getClaimedRequest()?.Id != claimed.Id)
            return true;

        if (sourceStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            setClaimedRequest(null);
            clearClaimMetadata();
            setStatus("Server already marked this route complete.");
        }
        else if (IsFailedAcquisitionStatus(sourceStatus))
        {
            var updated = claimed with { Status = sourceStatus };
            setClaimedRequest(updated);
            MarketAcquisitionClaimPersistence.Save(
                config,
                updated,
                getAcceptIdempotencyKey(),
                getRejectIdempotencyKey());
            setStatus("Server already marked this route failed. Restart to reopen the request.");
        }
        else if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(sourceStatus))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            setClaimedRequest(null);
            clearClaimMetadata();
            setStatus($"Server request moved to {sourceStatus}; fetch dashboard requests to continue.");
        }
        else
        {
            var updated = claimed with { Status = sourceStatus };
            setClaimedRequest(updated);
            MarketAcquisitionClaimPersistence.Save(
                config,
                updated,
                getAcceptIdempotencyKey(),
                getRejectIdempotencyKey());
            setStatus(getRouteStatusMessage());
        }

        saveConfig();
        return true;
    }

    private static bool TryExtractInvalidTransitionSourceStatus(string? error, out string sourceStatus)
    {
        const string prefix = "Cannot move acquisition request from ";
        const string separator = " to ";
        sourceStatus = string.Empty;
        if (string.IsNullOrWhiteSpace(error) ||
            !error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var start = prefix.Length;
        var end = error.IndexOf(separator, start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
            return false;

        sourceStatus = error[start..end].Trim();
        return !string.IsNullOrWhiteSpace(sourceStatus);
    }

    private static bool IsFailedAcquisitionStatus(string status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);
}
```

Add tests for complete, failed, unreportable, and stale-session conflicts. The stale-session case must return `true` without mutating persistence or the claimed request.

- [ ] **Step 4: Run reporting tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngineReportingTests|MarketAcquisitionRouteProgressReporter" --no-restore
```

Expected: reporting tests pass.

- [ ] **Step 5: Commit reporting extraction**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEnginePorts.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionClaimLifecycleController.cs .\tests\MarketMafioso.Tests\MarketAcquisition\MarketAcquisitionRouteEngineReportingTests.cs
git commit -m "refactor: move route reporting into engine"
```

---

### Task 10: Add Concrete Dalamud Adapters And Wire MainWindow

**Files:**
- Create: `src/MarketMafioso/MarketAcquisition/DalamudMarketAcquisitionRouteEngineAdapters.cs`
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
- Modify: `src/MarketMafioso/Windows/MarketAcquisitionPanels/MarketAcquisitionDiagnosticsPanel.cs`
- Modify: `src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`

- [ ] **Step 1: Add concrete adapters**

Create `src/MarketMafioso/MarketAcquisition/DalamudMarketAcquisitionRouteEngineAdapters.cs` with these concrete wrappers:

```csharp
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketAcquisitionRouteContext : IMarketAcquisitionRouteContext
{
    private readonly IPlayerState playerState;

    public DalamudMarketAcquisitionRouteContext(IPlayerState playerState)
    {
        this.playerState = playerState;
    }

    public bool IsCurrentWorldAvailable => playerState.CurrentWorld.IsValid;

    public string GetCurrentWorldName()
    {
        if (!playerState.CurrentWorld.IsValid)
            throw new InvalidOperationException("Current world is unavailable.");

        return playerState.CurrentWorld.Value.Name.ToString();
    }

    public bool TryGetCharacterScope(out string characterName, out string homeWorld)
    {
        characterName = playerState.CharacterName ?? string.Empty;
        homeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty;
        return !string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(homeWorld);
    }
}
```

Continue with UI automation:

```csharp
public sealed class DalamudMarketAcquisitionRouteUiAutomation : IMarketAcquisitionRouteUiAutomation
{
    public bool ProcessCommand(string command) => Plugin.CommandManager.ProcessCommand(command);

    public bool TryCloseMarketBoardWindows()
    {
        var closeRequested = false;
        closeRequested |= TryCloseAddon("ItemSearchResult");
        closeRequested |= TryCloseAddon("ItemSearch");
        return closeRequested || IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemSearch");
    }

    public AutomationTravelPreflightResult CheckTravelPreflight() =>
        AutomationTravelPreflight.Check(
            AutomationTravelPreflight.BlockingAddonNames
                .Where(IsAddonOpen)
                .ToArray());

    public unsafe bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message)
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult", 1);
        var probe = MarketBoardListingListProbe.Probe(addon, requestedRow);
        if (!probe.IsReady || probe.ComponentId == null)
        {
            message = $"Unable to request deeper market-board listings. {probe.Diagnostic}";
            return false;
        }

        var listingList = addon->AtkUnitBase.GetComponentListById(probe.ComponentId.Value);
        if (listingList == null)
        {
            message = $"Unable to request deeper market-board listings; list component {probe.ComponentId.Value} disappeared.";
            return false;
        }

        var row = (short)Math.Clamp(requestedRow, 0, short.MaxValue);
        listingList->ScrollToItem(row);
        message = $"Requested market-board listing row {requestedRow:N0}. {probe.Diagnostic}";
        return true;
    }

    private static unsafe bool TryCloseAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (!IsAddonOpen(addon))
            return false;

        addon->Close(true);
        return true;
    }

    private static unsafe bool IsAddonOpen(string addonName) =>
        IsAddonOpen(Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private static unsafe bool IsAddonOpen(AtkUnitBase* addon) =>
        addon != null && addon->IsReady && addon->IsVisible;
}
```

Add `DalamudMarketAcquisitionMarketBoardIo` and `DalamudMarketAcquisitionPurchaseIo` that wrap the current services. Keep wrappers thin.

- [ ] **Step 2: Wire engine in `MainWindow` constructor**

Add a field:

```csharp
private readonly MarketAcquisitionRouteEngine routeEngine;
```

Construct after existing route runner/purchase services are created:

```csharp
routeEngine = new MarketAcquisitionRouteEngine(
    marketAcquisitionRouteRunner,
    new DalamudMarketAcquisitionRouteContext(playerState),
    new DalamudMarketAcquisitionRouteUiAutomation(),
    new DalamudMarketAcquisitionMarketBoardIo(
        marketBoardApproachService,
        marketBoardItemSearchDriver,
        marketBoardListingReader,
        marketBoardInputCaptureReader),
    new DalamudMarketAcquisitionPurchaseIo(marketBoardPurchaseExecutor, marketBoardPurchaseAdapter),
    new MarketAcquisitionRouteRequestReporter(
        config,
        acquisitionClient,
        log),
    new MarketAcquisitionWorldVisitEvidenceRecorder(
        config,
        marketAcquisitionWorldVisitCatalog),
    new SystemMarketAcquisitionRouteClock());
```

Create `MarketAcquisitionRouteRequestReporter` and `MarketAcquisitionWorldVisitEvidenceRecorder` before wiring the engine. Keep them in `DalamudMarketAcquisitionRouteEngineAdapters.cs` for this slice, then split them into separate files only after the build is green.

- [ ] **Step 3: Replace `OnFrameworkUpdate` calls**

Change `OnFrameworkUpdate` to call:

```csharp
routeEngine.TickRoute(acquisitionRequestBusy);
routeEngine.MonitorMarketBoardPurchase(acquisitionRequestBusy);
```

Remove `MonitorGuidedRoute()` and `MonitorMarketBoardPurchase()` from `MainWindow` only after all callers are gone.

- [ ] **Step 4: Replace lifecycle callbacks**

Update guided-route panel construction to call engine methods through `MainWindow` wrappers or direct lambdas:

```csharp
() => _ = StartGuidedRouteAsync(false)
```

Inside the wrapper, keep dashboard claim readiness in `MainWindow`, then call:

```csharp
routeEngine.Start(plan, claimed, enableDiagnostics, config.EnableOpportunisticWorldChecks);
```

Pause/resume/stop/restart/reprepare should delegate to engine and then update any remaining `MainWindow` request status only where necessary.

- [ ] **Step 5: Replace diagnostics delegates with snapshot reads**

Where `MainWindow` passes:

```csharp
() => marketBoardReadResult
() => marketBoardReconciliation
() => marketAcquisitionLiveCandidatePlan
```

replace with:

```csharp
() => routeEngine.CreateSnapshot().MarketBoardReadResult
() => routeEngine.CreateSnapshot().MarketBoardReconciliation
() => routeEngine.CreateSnapshot().LiveCandidatePlan
```

Use a local snapshot variable in draw paths that call multiple values in one frame.

- [ ] **Step 6: Build**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 7: Commit MainWindow wiring**

Run:

```powershell
git add .\src\MarketMafioso\MarketAcquisition .\src\MarketMafioso\Windows\MainWindow.cs .\src\MarketMafioso\Windows\MarketAcquisitionPanels .\src\MarketMafioso\Windows\MarketAcquisitionDiagnosticsWindow.cs
git commit -m "refactor: wire acquisition route engine"
```

---

### Task 11: Remove Obsolete MainWindow Route Logic

**Files:**
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Remove moved fields**

Remove fields from `MainWindow` once all references point through `routeEngine`:

```csharp
private MarketBoardReadResult? marketBoardReadResult;
private MarketBoardListingReconciliation? marketBoardReconciliation;
private MarketAcquisitionLiveCandidatePlan? marketAcquisitionLiveCandidatePlan;
private uint activeWorldPurchasedQuantity;
private uint activeWorldSpentGil;
private string? activeWorldPurchaseBatchWorld;
private string? activePurchaseLineId;
private uint activeLinePurchasedQuantity;
private uint activeLineSpentGil;
private DateTimeOffset nextGuidedRouteMonitorUtc;
private bool guidedRouteProbeRunning;
private long guidedRouteProgressReportSequence;
private long guidedRouteProgressSessionVersion;
private string guidedRouteProgressNonce;
private string? lastGuidedRouteProgressReportKey;
```

- [ ] **Step 2: Remove moved methods**

Remove from `MainWindow` after references are gone:

- `MonitorGuidedRoute`
- `EnsureRouteTravelUiIsClear`
- `GetOpenRouteTravelBlockingAddons`
- `TryCloseMarketBoardWindows`
- `TryCloseAddon`
- `IsAddonOpen`
- `ProbeGuidedRouteMarketBoardAsync`
- `BeginNextWorldPurchase`
- `CompleteActiveWorldPurchaseBatch`
- `ResetMarketBoardStateForNextRouteItem`
- `ClearMarketBoardAutomationState`
- `MonitorMarketBoardPurchase`
- purchase snapshot helper methods
- `ProbeLiveMarketBoardCore`
- `TryContinueVisibleListingRead`
- `TryScrollMarketBoardListingsToRow`
- probe/purchase visit recording helpers
- `ReportGuidedRouteProgress`
- `ReportConfirmedPurchase`
- `ReportAcquisitionLineProgress`
- `ReportPurchaseAuditAsync`
- `ReportLineProgressAsync`
- `TryHandleRouteProgressConflict`
- `TryExtractInvalidTransitionSourceStatus`
- `ResolveActiveRouteLinePurchaseTotals`

Keep request pickup/claim/preparation methods in `MainWindow`.

- [ ] **Step 3: Search for stale references**

Run:

```powershell
rg -n "marketBoardReadResult|marketBoardReconciliation|marketAcquisitionLiveCandidatePlan|guidedRouteProbeRunning|activeWorldPurchasedQuantity|MonitorGuidedRoute|BeginNextWorldPurchase|MonitorMarketBoardPurchase|TryContinueVisibleListingRead|ReportGuidedRouteProgress" .\src\MarketMafioso\Windows\MainWindow.cs
```

Expected: no matches except intentional snapshot local variable names if used.

- [ ] **Step 4: Build**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 5: Commit cleanup**

Run:

```powershell
git add .\src\MarketMafioso\Windows\MainWindow.cs
git commit -m "refactor: remove route conductor from main window"
```

---

### Task 12: Franthropy Candidate Review Note

**Files:**
- Create: `docs/superpowers/specs/2026-07-10-market-acquisition-route-engine-franthropy-candidates.md`

- [ ] **Step 1: Write candidate note**

Create the file with:

```markdown
# Market Acquisition Route Engine Franthropy Candidate Review

## Move Later

- `MarketBoardPurchaseCandidate`
- `MarketBoardPurchaseRevalidation`
- `MarketBoardPurchasePlanner`
- `MarketBoardPurchaseSession`
- `MarketBoardAutomationController`
- market-board listing identity contracts

## Keep In MMF

- `MarketAcquisitionRouteEngine`
- dashboard claim lifecycle
- route progress and purchase audit server payloads
- Universalis freshness diagnostics
- world visit catalog policy
- route stop policy and request line semantics

## Decision

The first engine extraction stayed MMF-local. Franthropy migration should be a separate plan after the local engine survives live testing and route-log verification.
```

- [ ] **Step 2: Commit note**

Run:

```powershell
git add -f .\docs\superpowers\specs\2026-07-10-market-acquisition-route-engine-franthropy-candidates.md
git commit -m "docs: record route engine franthropy candidates"
```

---

### Task 13: Final Verification And Dev Deploy

**Files:**
- Verify all touched files.

- [ ] **Step 1: Scoped format**

Run:

```powershell
dotnet format .\src\MarketMafioso\MarketMafioso.csproj --include .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngine.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngineState.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngineSnapshot.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEnginePorts.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionRouteEngineTickResult.cs .\src\MarketMafioso\MarketAcquisition\MarketAcquisitionClaimLifecycleController.cs .\src\MarketMafioso\MarketAcquisition\DalamudMarketAcquisitionRouteEngineAdapters.cs .\src\MarketMafioso\Windows\MainWindow.cs --no-restore
```

Expected: command exits `0`.

- [ ] **Step 2: Build plugin**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 3: Run focused tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRouteEngine|MarketAcquisitionRouteRunner|MarketBoardAutomationController|MarketBoardPurchaseSession|MarketBoardPurchaseExecutor|MarketBoardListingReadAccumulator|MarketAcquisitionLiveCandidatePlanner|MarketAcquisitionRouteProgressReporter" --no-restore
```

Expected: focused tests pass.

- [ ] **Step 4: Run full plugin tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --no-restore
```

Expected: full plugin tests pass.

- [ ] **Step 5: Run server tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj --no-restore
```

Expected: server tests pass. This guards route progress/audit API contracts.

- [ ] **Step 6: Deploy dev plugin**

Run:

```powershell
.\src\MarketMafioso\tools\Deploy-DevPlugin.ps1
```

Expected: script succeeds, reports `Deploying MarketMafioso dev plugin from main@<sha>`, syncs to `C:\Users\gianf\AppData\Roaming\XIVLauncher\devPlugins\MarketMafioso`, and prints SHA256.

- [ ] **Step 7: Manual in-game smoke**

In game:

- Open `/mmf`.
- Verify Market Acquisition layout still looks the same.
- Start a route only after confirming accepted request/plan state is correct.
- Confirm diagnostics window still shows live read, reconciliation, candidate plan, and route diagnostic file paths.
- Confirm partial listing coverage still displays as incomplete coverage, not ordinary no-stock.
- If a route runs, verify `route.log`, `observed-listings.csv`, and `purchase-records.csv` are created when diagnostics are enabled.

- [ ] **Step 8: Final git status**

Run:

```powershell
git status -sb --untracked-files=all
git log --oneline -12
```

Expected: working tree clean except for intentional ignored runtime artifacts. Branch contains small commits from each extraction slice.

---

## Self-Review Notes

- The plan keeps the first extraction MMF-local.
- The route runner remains route session/diagnostics owner.
- `MainWindow` keeps request fetching, claiming, accepting, rejecting, and preparation in this pass.
- Characterization tests are added before each behavior move.
- Incomplete listing coverage is explicitly tested before purchase-pass migration.
- Reporting and claim conflict handling move together instead of leaving half of the side effects in `MainWindow`.
- Franthropy migration is documented as a follow-up, not mixed into this implementation.
