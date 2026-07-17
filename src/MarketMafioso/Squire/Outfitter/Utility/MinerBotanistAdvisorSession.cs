using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistAdvisorSessionStage
{
    Idle,
    ObservingStats,
    ObservingEquipment,
    DiscoveringMarket,
    Complete,
    Abstained,
    Failed,
    Cancelled,
}

public sealed record MinerBotanistAdvisorSessionState(
    MinerBotanistAdvisorSessionStage Stage,
    string Message,
    string CoverageLabel,
    int Completed,
    int? Total,
    MinerBotanistUtilityContextKind Context,
    MinerBotanistReadOnlyAdvice? Advice,
    bool AdviceIsRetained,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsBusy => Stage is MinerBotanistAdvisorSessionStage.ObservingStats or
        MinerBotanistAdvisorSessionStage.ObservingEquipment or MinerBotanistAdvisorSessionStage.DiscoveringMarket;
}

internal static class MinerBotanistAdvisorSessionEvidencePolicy
{
    public static OutfitterMarketEvidenceBook? SelectCurrent(
        OutfitterMarketDiscoveryResult result,
        OutfitterMarketEvidenceRequest request) =>
        result.WorkingBook.IsPublishable &&
        result.PublishedBook is { } published && published.Matches(request)
            ? published
            : null;
}

/// <summary>
/// Framework-ticked orchestration for the read-only advisor. Character identity, job, totals,
/// equipment identity, quality, and materia originate only in rendered UI observations.
/// </summary>
public sealed class MinerBotanistAdvisorSession : IDisposable
{
    private readonly IRenderedCharacterAdvisorProbe probe;
    private readonly LuminaRenderedEquipmentDefinitionLookup definitions;
    private readonly MinerBotanistAdvisorCatalog catalog;
    private readonly OutfitterMarketEvidenceDiscoveryService marketDiscovery;
    private readonly MinerBotanistReadOnlyAdvisor advisor = new();
    private CancellationTokenSource? cancellation;
    private Task<OutfitterMarketDiscoveryResult>? discoveryTask;
    private OutfitterMarketEvidenceRequest? discoveryRequest;
    private RenderedGatheringStatsObservation? renderedStats;
    private RenderedEquipmentScanProgress? renderedEquipment;
    private RenderedMinerBotanistBaseline? baseline;
    private RenderedEquipmentResolution? resolution;
    private MinerBotanistAdvisorCatalogResult? offers;
    private DateTimeOffset stageDeadlineUtc;
    private bool characterUiOpenedBySession;
    private bool characterUiRestoreRequested;
    private DateTimeOffset characterUiRestoreDeadlineUtc;

    public MinerBotanistAdvisorSession(
        IRenderedCharacterAdvisorProbe probe,
        IDataManager dataManager,
        IMarketAcquisitionListingSource listingSource,
        string evidencePath)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(listingSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        definitions = new(dataManager);
        catalog = new(dataManager);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        marketDiscovery = new(
            listingSource,
            new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), maxEntries: 4096),
            new OutfitterMarketEvidenceFileStore(evidencePath));
        State = Idle(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
    }

    public MinerBotanistAdvisorSessionState State { get; private set; }

    public void Begin(MinerBotanistUtilityContextKind context, string region)
    {
        CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Superseded by a new advisor refresh.");
        var retainedOwnedCharacterUi = characterUiOpenedBySession;
        var retainedAdvice = State.Context == context && State.Advice is { Frontier: not null }
            ? State.Advice
            : null;
        var retainedCoverage = retainedAdvice is null
            ? "Coverage is declared after the rendered job is known."
            : State.CoverageLabel;
        cancellation = new();
        discoveryTask = null;
        discoveryRequest = null;
        renderedStats = null;
        renderedEquipment = null;
        baseline = null;
        resolution = null;
        offers = null;
        probe.PrepareAdvisorObservation();
        characterUiOpenedBySession = retainedOwnedCharacterUi || probe.Open();
        characterUiRestoreRequested = false;
        characterUiRestoreDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        stageDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        State = new(
            MinerBotanistAdvisorSessionStage.ObservingStats,
            "Reading a stable MIN/BTN level and stat tuple from the Character window.",
            retainedCoverage,
            0,
            null,
            context,
            retainedAdvice,
            retainedAdvice is not null,
            DateTimeOffset.UtcNow);
        Region = string.IsNullOrWhiteSpace(region) ? "North America" : region.Trim();
    }

    public string Region { get; private set; } = "North America";

    public void Tick()
    {
        TryRestoreCharacterUi();
        if (!State.IsBusy)
            return;
        try
        {
            switch (State.Stage)
            {
                case MinerBotanistAdvisorSessionStage.ObservingStats:
                    TickStats();
                    break;
                case MinerBotanistAdvisorSessionStage.ObservingEquipment:
                    TickEquipment();
                    break;
                case MinerBotanistAdvisorSessionStage.DiscoveringMarket:
                    TickMarket();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor refresh was cancelled.");
        }
        catch (Exception exception)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Failed, $"Advisor refresh failed safely: {exception.Message}");
        }
    }

    public void Cancel() => CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor refresh was cancelled.");

    private void TickStats()
    {
        renderedStats = probe.CaptureGatheringStats();
        State = State with { Message = renderedStats.Diagnostic, UpdatedAtUtc = DateTimeOffset.UtcNow };
        if (renderedStats.Status != RenderedCharacterObservationStatus.Complete)
        {
            if (DateTimeOffset.UtcNow >= stageDeadlineUtc)
                Abstain($"Rendered Character observation did not become complete within 15 seconds: {renderedStats.Diagnostic}");
            return;
        }
        if (renderedStats.Level != 100)
        {
            Abstain("The first advisor release supports only a rendered level-100 Miner or Botanist.");
            return;
        }
        renderedEquipment = probe.BeginEquipmentScan();
        if (renderedEquipment.Status != RenderedEquipmentScanStatus.ReadyToHover)
        {
            Abstain(renderedEquipment.Diagnostic);
            return;
        }
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.ObservingEquipment,
            Message = renderedEquipment.Diagnostic,
            Completed = 0,
            Total = renderedEquipment.TotalSlots,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void TickEquipment()
    {
        var step = probe.AdvanceEquipmentScan();
        renderedEquipment = step.Progress;
        State = State with
        {
            Message = step.Message,
            Completed = renderedEquipment.CompletedSlots,
            Total = renderedEquipment.TotalSlots,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        if (renderedEquipment.Status == RenderedEquipmentScanStatus.Failed)
        {
            Abstain(renderedEquipment.Diagnostic);
            return;
        }
        if (renderedEquipment.Status != RenderedEquipmentScanStatus.Complete)
            return;

        RequestCharacterUiRestore();

        baseline = RenderedMinerBotanistBaselineAssembler.Assemble(renderedStats!, renderedEquipment);
        if (baseline.Status != RenderedMinerBotanistBaselineStatus.Complete || baseline.ClassJobId is not { } classJobId)
        {
            Abstain(baseline.Diagnostic);
            return;
        }
        resolution = RenderedEquipmentDefinitionResolver.Resolve(
            renderedEquipment.Observations,
            classJobId,
            definitions.FindByExactName);
        if (resolution.Status != RenderedEquipmentResolutionStatus.Complete)
        {
            Abstain(resolution.Diagnostic);
            return;
        }

        offers = catalog.Build(classJobId);
        if (offers.MarketItemIds.Count == 0)
        {
            Abstain("The declared market scope contains no eligible items in this game-data version.");
            return;
        }
        discoveryRequest = new(
            "universalis",
            Region,
            offers.MarketItemIds,
            ListingLimit: 100,
            CoverageMode: OutfitterMarketCoverageMode.ExhaustiveWithinScope,
            MaxConcurrency: 4);
        discoveryTask = marketDiscovery.DiscoverAsync(discoveryRequest, cancellation!.Token);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.DiscoveringMarket,
            Message = $"Discovering exact NQ/HQ listings for {offers.MarketItemIds.Count:N0} scoped items.",
            CoverageLabel = offers.CoverageLabel,
            Completed = 0,
            Total = offers.MarketItemIds.Count,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void TickMarket()
    {
        if (discoveryRequest is not null && marketDiscovery.GetLiveState(discoveryRequest) is { } live)
        {
            var retainedAdvice = State.Advice;
            if (retainedAdvice is null && live.PreviousPublishedBook is { } previous)
                retainedAdvice = BuildAdvice(previous);
            State = State with
            {
                Message = live.Progress.Message,
                Completed = live.Progress.Completed,
                Total = live.Progress.Total,
                Advice = retainedAdvice is { Frontier: not null } ? retainedAdvice : State.Advice,
                AdviceIsRetained = retainedAdvice is { Frontier: not null },
                UpdatedAtUtc = live.Progress.UpdatedAtUtc,
            };
        }
        if (discoveryTask is not { IsCompleted: true })
            return;

        var result = discoveryTask.GetAwaiter().GetResult();
        var evidence = result.WorkingBook;
        var currentEvidence = MinerBotanistAdvisorSessionEvidencePolicy.SelectCurrent(result, discoveryRequest!);
        var advice = currentEvidence is not null
            ? BuildAdvice(currentEvidence)
            : advisor.Build(
                baseline!,
                resolution!,
                evidence,
                itemId => offers!.Definitions.TryGetValue(itemId, out var definition) ? [definition] : [],
                State.Context,
                offers!.VendorOffers);
        var stage = advice.Status == MinerBotanistAdvisorStatus.Complete
            ? MinerBotanistAdvisorSessionStage.Complete
            : MinerBotanistAdvisorSessionStage.Abstained;
        var retainPrevious = advice.Status != MinerBotanistAdvisorStatus.Complete && State.Advice is { Frontier: not null };
        State = State with
        {
            Stage = stage,
            Message = retainPrevious
                ? $"Refresh abstained: {advice.Diagnostic} The last valid frontier remains visible."
                : advice.Diagnostic,
            Completed = evidence.Coverage.QueriedItemCount,
            Total = evidence.Coverage.CatalogItemCount,
            Advice = retainPrevious ? State.Advice : advice,
            AdviceIsRetained = retainPrevious,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private MinerBotanistReadOnlyAdvice BuildAdvice(OutfitterMarketEvidenceBook evidence) => advisor.Build(
        baseline!,
        resolution!,
        evidence,
        itemId => offers!.Definitions.TryGetValue(itemId, out var definition) ? [definition] : [],
        State.Context,
        offers!.VendorOffers);

    private void Abstain(string message)
    {
        RequestCharacterUiRestore();
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Abstained,
            Message = message,
            AdviceIsRetained = State.Advice is { Frontier: not null },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void CancelCore(MinerBotanistAdvisorSessionStage terminalStage, string message)
    {
        if (cancellation is null && !State.IsBusy)
            return;
        cancellation?.Cancel();
        RequestCharacterUiRestore();
        State = State with
        {
            Stage = terminalStage,
            Message = message,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void DisposeCancellation()
    {
        cancellation?.Dispose();
        cancellation = null;
    }

    private void RequestCharacterUiRestore()
    {
        probe.CancelEquipmentScan();
        characterUiRestoreRequested = characterUiOpenedBySession;
        TryRestoreCharacterUi();
    }

    private void TryRestoreCharacterUi()
    {
        if (!characterUiRestoreRequested)
            return;
        if (probe.TryCloseCharacterUi() || DateTimeOffset.UtcNow >= characterUiRestoreDeadlineUtc)
        {
            characterUiOpenedBySession = false;
            characterUiRestoreRequested = false;
        }
    }

    private static MinerBotanistAdvisorSessionState Idle(MinerBotanistUtilityContextKind context) => new(
        MinerBotanistAdvisorSessionStage.Idle,
        "Refresh to build read-only advice from rendered Character UI without activating FFXIV.",
        "Coverage has not been observed yet.",
        0,
        null,
        context,
        null,
        false,
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }
}
