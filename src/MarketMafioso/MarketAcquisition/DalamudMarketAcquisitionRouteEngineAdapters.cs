using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketAcquisitionRouteCallbackDispatcher : IMarketAcquisitionRouteCallbackDispatcher
{
    public Task DispatchAsync(Action callback) => Plugin.Framework.RunOnTick(callback);
}

public sealed class DalamudMarketAcquisitionRouteContext : IMarketAcquisitionRouteContext
{
    private readonly IPlayerState playerState;

    public DalamudMarketAcquisitionRouteContext(IPlayerState playerState) => this.playerState = playerState;

    public bool IsCurrentWorldAvailable => playerState.CurrentWorld.IsValid;

    public string GetCurrentWorldName() => IsCurrentWorldAvailable
        ? playerState.CurrentWorld.Value.Name.ToString()
        : throw new InvalidOperationException("Current world is unavailable.");

    public bool TryGetCharacterScope(out string characterName, out string homeWorld)
    {
        characterName = playerState.CharacterName ?? string.Empty;
        homeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty;
        return !string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(homeWorld);
    }
}

public sealed class DalamudMarketAcquisitionRouteUiAutomation : IMarketAcquisitionRouteUiAutomation
{
    public bool ProcessCommand(string command) => Plugin.CommandManager.ProcessCommand(command);

    public bool TryCloseMarketBoardWindows()
    {
        var closeRequested = TryCloseAddon("ItemSearchResult") | TryCloseAddon("ItemSearch");
        return closeRequested || IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemSearch");
    }

    public AutomationTravelPreflightResult CheckTravelPreflight() =>
        AutomationTravelPreflight.Check(AutomationTravelPreflight.BlockingAddonNames.Where(IsAddonOpen).ToArray());

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

        listingList->ScrollToItem((short)Math.Clamp(requestedRow, 0, short.MaxValue));
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

    private static unsafe bool IsAddonOpen(string addonName) => IsAddonOpen(Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private static unsafe bool IsAddonOpen(AtkUnitBase* addon) => addon != null && addon->IsReady && addon->IsVisible;
}

public sealed class DalamudMarketAcquisitionMarketBoardIo : IMarketAcquisitionMarketBoardIo
{
    private readonly MarketBoardApproachService approachService;
    private readonly MarketBoardItemSearchDriver searchDriver;
    private readonly MarketBoardListingReader listingReader;
    private readonly MarketBoardInputCaptureReader inputCaptureReader;

    public DalamudMarketAcquisitionMarketBoardIo(
        MarketBoardApproachService approachService,
        MarketBoardItemSearchDriver searchDriver,
        MarketBoardListingReader listingReader,
        MarketBoardInputCaptureReader inputCaptureReader)
    {
        this.approachService = approachService;
        this.searchDriver = searchDriver;
        this.listingReader = listingReader;
        this.inputCaptureReader = inputCaptureReader;
    }

    public MarketBoardApproachResult OpenOrApproachMarketBoard() => approachService.OpenOrApproach();
    public MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName) => searchDriver.Search(itemId, itemName);
    public MarketBoardReadResult ReadCurrentListings(string currentWorld) => listingReader.ReadCurrentListings(currentWorld);
    public MarketBoardInputCapture CaptureInputState() => inputCaptureReader.Capture();
}

public sealed class DalamudMarketAcquisitionPurchaseIo : IMarketAcquisitionPurchaseIo
{
    private readonly MarketBoardPurchaseExecutor executor;
    private readonly DalamudMarketBoardPurchaseAdapter adapter;

    public DalamudMarketAcquisitionPurchaseIo(MarketBoardPurchaseExecutor executor, DalamudMarketBoardPurchaseAdapter adapter)
    {
        this.executor = executor;
        this.adapter = adapter;
    }

    public MarketBoardPurchaseResult ExecuteFirstCandidate(MarketAcquisitionLiveCandidatePlan candidatePlan, MarketBoardReadResult freshRead) =>
        executor.ExecuteFirstCandidate(candidatePlan, freshRead);

    public MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate) =>
        adapter.TryConfirmPendingPurchase(candidate);
}

public sealed class MarketAcquisitionRouteRequestReporter : IMarketAcquisitionRouteReporter
{
    private readonly Configuration config;
    private readonly MarketAcquisitionRequestClient client;

    public MarketAcquisitionRouteRequestReporter(Configuration config, MarketAcquisitionRequestClient client)
    {
        this.config = config;
        this.client = client;
    }

    public bool CanReport => !string.IsNullOrWhiteSpace(config.ServerUrl) && !string.IsNullOrWhiteSpace(config.ApiKey);

    public async Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(MarketAcquisitionRouteProgressReport report, CancellationToken cancellationToken)
    {
        var action = MarketAcquisitionRouteProgressReporter.ResolveAction(report.RouteState);
        var result = action switch
        {
            MarketAcquisitionRouteProgressReporter.FailAction => await client.FailAttemptAsync(config.ServerUrl, config.ApiKey, report.RequestId, report.ClaimToken, config.PluginInstanceId, report.AttemptId, report.Sequence, report.RouteStopId, report.ActiveWorld, report.Phase, report.Message, PluginBuildInfo.DisplayVersion, cancellationToken).ConfigureAwait(false),
            MarketAcquisitionRouteProgressReporter.CompleteAction => await client.CompleteAttemptAsync(config.ServerUrl, config.ApiKey, report.RequestId, report.ClaimToken, config.PluginInstanceId, report.AttemptId, report.Sequence, report.RouteStopId, report.ActiveWorld, report.Phase, report.Message, PluginBuildInfo.DisplayVersion, cancellationToken).ConfigureAwait(false),
            _ => await client.ReportAttemptProgressAsync(config.ServerUrl, config.ApiKey, report.RequestId, report.ClaimToken, config.PluginInstanceId, report.AttemptId, report.Sequence, report.RouteStopId, report.ActiveWorld, report.Phase, report.Message, PluginBuildInfo.DisplayVersion, cancellationToken).ConfigureAwait(false),
        };
        return new MarketAcquisitionRouteProgressReportOutcome(action, result.Request);
    }

    public async Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken)
    {
        await client.PostPurchaseAuditAsync(config.ServerUrl, config.ApiKey, report.RequestId, new MarketAcquisitionPurchaseAuditRequest
        {
            ClaimToken = report.ClaimToken,
            IdempotencyKey = $"{config.PluginInstanceId}:{report.AttemptId}:purchase:{report.Sequence}",
            AttemptId = report.AttemptId,
            Sequence = report.Sequence,
            LineId = report.LineId,
            WorldName = report.WorldName,
            ItemId = report.ItemId,
            ItemName = report.ItemName,
            ListingId = report.Candidate.ListingId,
            RetainerName = report.Candidate.RetainerName,
            RetainerId = report.Candidate.RetainerId,
            Quantity = report.Candidate.Quantity,
            UnitPrice = report.Candidate.UnitPrice,
            TotalGil = report.Candidate.TotalGil,
            IsHq = report.Candidate.IsHq,
            Result = "Purchased",
            Message = report.Message,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken)
    {
        await client.PostLineProgressAsync(config.ServerUrl, config.ApiKey, report.RequestId, report.LineId, new MarketAcquisitionLineProgressRequest
        {
            ClaimToken = report.ClaimToken,
            IdempotencyKey = $"{config.PluginInstanceId}:{report.AttemptId}:line:{report.LineId}:{report.Sequence}",
            AttemptId = report.AttemptId,
            Sequence = report.Sequence,
            Status = report.Status,
            PurchasedQuantity = report.PurchasedQuantity,
            SpentGil = report.SpentGil,
            Message = report.Message,
            Reason = report.Reason,
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MarketAcquisitionWorldVisitEvidenceRecorder : IMarketAcquisitionRouteEvidenceRecorder
{
    private const int MaxRecords = 2_000;
    private readonly Configuration config;
    private readonly MarketAcquisitionWorldVisitCatalog catalog;

    public MarketAcquisitionWorldVisitEvidenceRecorder(Configuration config, MarketAcquisitionWorldVisitCatalog catalog)
    {
        this.config = config;
        this.catalog = catalog;
    }

    public void RecordProbeVisit(string currentWorld, MarketAcquisitionRequestView activeLine, MarketAcquisitionWorldItemSubtask? activeSubtask, MarketAcquisitionLiveCandidatePlan candidatePlan, string? requestId, string routeRunId)
    {
        var legalRows = candidatePlan.Rows.Where(row => row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase)).ToArray();
        catalog.RecordProbe(new MarketAcquisitionWorldVisitRecord
        {
            WorldName = currentWorld,
            DataCenter = ResolveDataCenter(currentWorld, activeSubtask?.DataCenter),
            ItemId = activeSubtask?.ItemId ?? activeLine.ItemId,
            ItemName = activeSubtask?.ItemName ?? activeLine.ItemName,
            HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(activeSubtask?.HqPolicy ?? activeLine.HqPolicy),
            MaxUnitPrice = activeSubtask?.MaxUnitPrice ?? activeLine.MaxUnitPrice,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Result = candidatePlan.WouldBuyQuantity > 0 ? MarketAcquisitionLiveCandidateStatuses.LegalStockObserved : candidatePlan.Status,
            ObservedLegalListingCount = legalRows.Length,
            ObservedLegalQuantity = (uint)legalRows.Sum(row => row.LiveListing.Quantity),
            ObservedLegalGil = legalRows.Aggregate(0ul, (total, row) => checked(total + ((ulong)row.LiveListing.UnitPrice * row.LiveListing.Quantity))),
            Source = "LiveMarketBoardProbe",
            RequestId = requestId,
            RouteRunId = routeRunId,
            RouteStopId = $"{ResolveDataCenter(currentWorld, activeSubtask?.DataCenter)}:{currentWorld}",
        });
        Save();
    }

    public void RecordPurchaseVisit(MarketBoardPurchaseCandidate candidate, MarketAcquisitionWorldItemSubtask activeSubtask, string worldName, string? requestId, string routeRunId)
    {
        var dataCenter = ResolveDataCenter(worldName, activeSubtask.DataCenter);
        catalog.RecordProbe(new MarketAcquisitionWorldVisitRecord
        {
            WorldName = worldName,
            DataCenter = dataCenter,
            ItemId = activeSubtask.ItemId,
            ItemName = activeSubtask.ItemName,
            HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(activeSubtask.HqPolicy),
            MaxUnitPrice = activeSubtask.MaxUnitPrice,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Result = MarketAcquisitionLiveCandidateStatuses.Purchased,
            PurchasedQuantity = candidate.Quantity,
            SpentGil = candidate.TotalGil,
            ObservedLegalListingCount = 1,
            ObservedLegalQuantity = candidate.Quantity,
            ObservedLegalGil = candidate.TotalGil,
            Source = "PurchaseAudit",
            RequestId = requestId,
            RouteRunId = routeRunId,
            RouteStopId = $"{dataCenter}:{worldName}",
        });
        Save();
    }

    private void Save()
    {
        catalog.Prune(MaxRecords);
        config.Save();
    }

    private static string ResolveDataCenter(string worldName, string? plannedDataCenter) =>
        !string.IsNullOrWhiteSpace(plannedDataCenter) ? plannedDataCenter : MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);
}
