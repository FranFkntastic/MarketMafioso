using Franthropy.Dalamud.Runtime;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionPriceCompleteEngineTests
{
    [Fact]
    public void AllOverpriced_ClosesTheOwnedBrowseWithoutPurchasing()
    {
        using var fixture = new Fixture(120, 130);
        fixture.Tick();
        Assert.Equal("Completed", fixture.Runner.State);
        Assert.Equal("browse:1", fixture.Ports.ClosedBrowse);
        Assert.Equal(0, fixture.Ports.PurchaseCalls);
        Assert.Equal(0, fixture.Ports.ScrollCalls);
    }

    [Fact]
    public void MixedPrefix_PurchasesWithoutClosingOrContinuingListings()
    {
        using var fixture = new Fixture(80, 100, 120);
        fixture.Tick();
        Assert.Equal("Purchasing", fixture.Runner.ActiveStop?.Status);
        Assert.Equal(1, fixture.Ports.PurchaseCalls);
        Assert.Equal(80u, fixture.Ports.LastCandidate?.UnitPrice);
        Assert.Null(fixture.Ports.ClosedBrowse);
        Assert.Equal(0, fixture.Ports.ScrollCalls);
    }

    [Fact]
    public void RecoverableSelection_UsesFreshGenerationAndAppliesTheSameCutoff()
    {
        using var fixture = new Fixture(80, 120);
        fixture.Ports.RejectNextSelection = true;
        fixture.Tick();
        Assert.Equal(1, fixture.Ports.PurchaseCalls);
        fixture.Engine.BeginNextWorldPurchase();
        Assert.Equal(2, fixture.Ports.PurchaseCalls);
        Assert.Equal("browse:2", fixture.Ports.LastPurchaseBrowse);
        Assert.Equal("browse:1", fixture.Ports.RefreshPreviousBrowse);
        Assert.Equal(0, fixture.Ports.ScrollCalls);
    }

    [Fact]
    public void ExhaustiveSearch_RemainsPinnedAndDoesNotPurchasePartialPrefix()
    {
        using var fixture = new Fixture(80, 120);
        fixture.Exhaustive = true;
        fixture.Tick();
        fixture.Exhaustive = false;
        fixture.Tick();
        Assert.Equal(0, fixture.Ports.PurchaseCalls);
        Assert.Null(fixture.Ports.ClosedBrowse);
        fixture.Ports.BrowseSnapshot = fixture.Ports.BrowseSnapshot with { Phase = MarketBoardBrowsePhase.Completed };
        fixture.Ports.Read = fixture.Ports.Read with
        {
            Status = "Ready", ReadState = MarketBoardListingReadState.FreshComplete,
            ReportedListingCount = 2, IsListingCountTruncated = false,
        };
        fixture.Tick();
        Assert.Equal(1, fixture.Ports.PurchaseCalls);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Ports Ports;
        public readonly MarketAcquisitionRouteRunner Runner = new(Path.GetTempPath());
        public readonly MarketAcquisitionRouteEngine Engine;
        private readonly FramePacingGovernor governor = new();
        public bool Exhaustive;

        public Fixture(params uint[] prices)
        {
            Ports = new Ports(prices);
            var config = new Configuration();
            var lifecycle = new MarketAcquisitionClaimLifecycleController(config,
                () => null, _ => { }, () => null, () => null, () => { }, _ => { }, () => string.Empty, () => { });
            Engine = new MarketAcquisitionRouteEngine(Runner, Ports, Ports,
                new UnsupportedMarketAcquisitionRouteTravelCleanup(), Ports, Ports, Ports, Ports,
                lifecycle, Ports, Ports, new MarketAcquisitionTravelFrameThrottle(governor),
                new ConfigurationExactAcquisitionRouteExecutionStateStore(config, () => { }),
                exhaustiveResearchModeProvider: () => Exhaustive);
            var claim = new MarketAcquisitionClaimView
            {
                Id = "prefix-engine", ItemId = 5059, ItemName = "Cobalt Ingot",
                QuantityMode = "AllBelowThreshold", HqPolicy = "Either", MaxUnitPrice = 100, MaxTotalGil = 10000,
            };
            var plan = new MarketAcquisitionPlan
            {
                RequestId = claim.Id, Status = "Ready", ItemId = 5059,
                Lines = [new() { LineId = "line:1", ItemId = 5059, ItemName = claim.ItemName, Status = "Ready", QuantityMode = "AllBelowThreshold" }],
                WorldBatches = [new()
                {
                    WorldName = "Siren", DataCenter = "Aether",
                    ItemSubtasks = [new()
                    {
                        LineId = "line:1", ItemId = 5059, ItemName = claim.ItemName, WorldName = "Siren", DataCenter = "Aether",
                        QuantityMode = "AllBelowThreshold", HqPolicy = "Either", MaxUnitPrice = 100, GilCap = 10000, Source = "Planned",
                    }],
                }],
            };
            var start = Engine.Start(plan, claim, false, false);
            Assert.True(start.Success, start.Message);
            Assert.True(Runner.RecordCurrentWorld("Siren").Success);
        }

        public void Tick()
        {
            Ports.UtcNow += TimeSpan.FromSeconds(1);
            Engine.TickRoute(false);
            Assert.True(Runner.State != "Failed", Runner.StatusMessage);
        }

        public void Dispose() { Engine.Dispose(); Runner.Dispose(); governor.Dispose(); }
    }

    private sealed class Ports : IMarketAcquisitionRouteContext, IMarketAcquisitionRouteUiAutomation,
        IMarketAcquisitionMarketBoardIo, IMarketAcquisitionPurchaseIo, IMarketAcquisitionRouteReporter,
        IMarketAcquisitionRouteEvidenceRecorder, IMarketAcquisitionRouteCallbackDispatcher, IMarketAcquisitionRouteClock
    {
        public Ports(uint[] prices)
        {
            Read = new MarketBoardReadResult
            {
                Status = "VerifiedListingPrefix", ReadState = MarketBoardListingReadState.FreshPartial,
                ItemId = 5059, WorldName = "Siren", BrowseOperationId = "browse:1",
                ReportedListingCount = 20, ListingCapacity = 100, IsListingCountTruncated = true,
                BrowseExpectedPageCount = 2, BrowseObservedPageCount = 1,
                Listings = prices.Select((price, i) => new MarketBoardLiveListing
                {
                    ItemId = 5059, WorldName = "Siren", UnitPrice = price, Quantity = 1,
                    ListingId = $"listing:{i}", RetainerId = $"retainer:{i}",
                }).ToArray(),
            };
        }
        public MarketBoardReadResult Read;
        public MarketBoardBrowseSnapshot BrowseSnapshot { get; set; } = new()
        {
            Owner = MarketBoardBrowseOwner.MarketAcquisition, ItemId = 5059,
            OperationId = "browse:1", Phase = MarketBoardBrowsePhase.AwaitingPagesAndHistory,
            RequestAccepted = true, HeaderObserved = true,
        };
        public int PurchaseCalls, ScrollCalls;
        public bool RejectNextSelection;
        public string? ClosedBrowse, RefreshPreviousBrowse, LastPurchaseBrowse;
        public MarketBoardPurchaseCandidate? LastCandidate;
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public long MonotonicMilliseconds => UtcNow.ToUnixTimeMilliseconds();
        public bool IsCurrentWorldAvailable => true;
        public string GetCurrentWorldName() => "Siren";
        public bool TryGetCharacterScope(out string characterName, out string homeWorld) { characterName = "Fixture"; homeWorld = "Siren"; return true; }
        public bool TryIsWorldTravelBusy(out bool isBusy) { isBusy = false; return true; }
        public bool ProcessCommand(string command) => throw new InvalidOperationException("No travel expected.");
        public bool TryCloseMarketBoardWindows() => false;
        public AutomationTravelPreflightResult CheckTravelPreflight() => AutomationTravelPreflight.Check([]);
        public bool TryScrollMarketBoardListingsToRow(int row, out string message) { ScrollCalls++; message = "Unexpected continuation"; return false; }
        public bool IsMarketBoardReady() => true;
        public MarketBoardItemSearchResult SearchItem(uint itemId, string? name, MarketBoardItemSearchIntent intent = MarketBoardItemSearchIntent.PresentOrBrowse, string? previousOperationId = null)
        {
            if (intent == MarketBoardItemSearchIntent.RequireFreshBrowse)
            {
                RefreshPreviousBrowse = previousOperationId;
                BrowseSnapshot = BrowseSnapshot with { OperationId = "browse:2" };
                Read = Read with { BrowseOperationId = "browse:2" };
            }
            return new() { Status = BrowseSnapshot.IsComplete ? "ListingsReady" : "AwaitingBrowseEvidence", Message = "Fixture browse evidence.", BrowseEvidence = BrowseSnapshot };
        }
        public MarketBoardReadResult ReadCurrentListings(string world) => Read;
        public MarketBoardInputCapture CaptureInputState() => new();
        public void AbandonBrowse(string reason) { }
        public bool CloseBrowse(string operationId) { ClosedBrowse = operationId; return true; }
        public MarketBoardPurchaseResult ExecuteFirstCandidate(MarketAcquisitionLiveCandidatePlan plan, MarketBoardReadResult read)
        {
            PurchaseCalls++;
            LastPurchaseBrowse = read.BrowseOperationId;
            LastCandidate = MarketBoardPurchasePlanner.SelectFirstFreshSafeCandidate(plan, read);
            var reject = RejectNextSelection;
            RejectNextSelection = false;
            return new() { Status = reject ? "ListingMissing" : "PurchaseSelectionSent", Candidate = LastCandidate };
        }
        public MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate) => throw new InvalidOperationException("No confirmation expected.");
        public bool CanReport => false;
        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken token) => throw new InvalidOperationException();
        public Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken token) => Task.CompletedTask;
        public Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken token) => Task.CompletedTask;
        public Task ReportMarketObservationAsync(MarketAcquisitionMarketObservationReport report, CancellationToken token) => Task.CompletedTask;
        public void RecordProbeVisit(string world, MarketAcquisitionRequestView line, MarketAcquisitionWorldItemSubtask? subtask, MarketAcquisitionLiveCandidatePlan plan, string? request, string run) { }
        public void RecordPurchaseVisit(MarketBoardPurchaseCandidate candidate, MarketAcquisitionWorldItemSubtask subtask, string world, string? request, string run) { }
        public Task DispatchAsync(Action action) { action(); return Task.CompletedTask; }
    }
}
