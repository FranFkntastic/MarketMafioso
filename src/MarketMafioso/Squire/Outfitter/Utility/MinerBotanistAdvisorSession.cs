using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
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
#if DEBUG
    private readonly string solverReplayPath;
#endif
    private readonly MinerBotanistReadOnlyAdvisor advisor = new();
    private readonly GenerationBoundComputation<MinerBotanistReadOnlyAdvice> solving = new();
    private CancellationTokenSource? cancellation;
    private Task<OutfitterMarketDiscoveryResult>? discoveryTask;
    private OutfitterMarketEvidenceRequest? discoveryRequest;
    private RenderedGatheringStatsObservation? renderedStats;
    private RenderedEquipmentScanProgress? renderedEquipment;
    private RenderedMinerBotanistBaseline? baseline;
    private RenderedEquipmentResolution? resolution;
    private MinerBotanistAdvisorCatalogResult? offers;
    private OutfitterMarketEvidenceBook? pendingCurrentEvidence;
    private RenderedPlayerAuthorityFingerprint? advicePlayerFingerprint;
    private WorkbenchValidationRequest? workbenchValidationRequest;
    private OutfitterWorkbenchPlayerValidation? completedWorkbenchValidation;
    private SolverProgressSnapshot? solverProgress;
    private long sessionGeneration;
    private DateTimeOffset solvingStartedAtUtc;
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
#if DEBUG
        solverReplayPath = Path.Combine(Path.GetDirectoryName(evidencePath)!, "outfitter-solver-replay.json");
#endif
        marketDiscovery = new(
            listingSource,
            new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), maxEntries: 4096),
            new OutfitterMarketEvidenceFileStore(evidencePath));
        State = Idle(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
    }

    public MinerBotanistAdvisorSessionState State { get; private set; }

    public OutfitterMarketEvidenceBook? CurrentEvidence { get; private set; }

    public void Begin(MinerBotanistUtilityContextKind context, string region)
    {
        CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Superseded by a new advisor refresh.");
        sessionGeneration++;
        var retainedOwnedCharacterUi = characterUiOpenedBySession;
        var retainedAdvice = State.Context == context && State.Advice is { Frontier: not null }
            ? State.Advice
            : null;
        if (retainedAdvice is null)
            CurrentEvidence = null;
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
        pendingCurrentEvidence = null;
        advicePlayerFingerprint = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        Volatile.Write(ref solverProgress, null);
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

    public void InvalidateForPlayerStateChange()
    {
        cancellation?.Cancel();
        sessionGeneration++;
        solving.Invalidate();
        pendingCurrentEvidence = null;
        advicePlayerFingerprint = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        Volatile.Write(ref solverProgress, null);
        CurrentEvidence = null;
        RequestCharacterUiRestore();
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Cancelled,
            Message = "The rendered player gearset changed; refresh before using Advisor evidence.",
            Advice = null,
            AdviceIsRetained = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    internal bool RequestWorkbenchValidation(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSolutionId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (State.Stage != MinerBotanistAdvisorSessionStage.Complete || State.AdviceIsRetained ||
            !ReferenceEquals(advice, State.Advice) || !ReferenceEquals(evidence, CurrentEvidence) ||
            advicePlayerFingerprint is not { } capturedPlayer)
        {
            return false;
        }

        workbenchValidationRequest = new(advice, selectedSolutionId, evidence, capturedPlayer);
        completedWorkbenchValidation = null;
        cancellation = new();
        renderedStats = null;
        renderedEquipment = null;
        baseline = null;
        resolution = null;
        probe.PrepareAdvisorObservation();
        characterUiOpenedBySession = probe.Open();
        characterUiRestoreRequested = false;
        characterUiRestoreDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        stageDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.ObservingStats,
            Message = "Revalidating the rendered job and equipment before Workbench review.",
            Completed = 0,
            Total = null,
            AdviceIsRetained = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        return true;
    }

    internal bool TryTakeWorkbenchValidation(out OutfitterWorkbenchPlayerValidation validation)
    {
        if (completedWorkbenchValidation is null)
        {
            validation = null!;
            return false;
        }

        validation = completedWorkbenchValidation;
        completedWorkbenchValidation = null;
        return true;
    }

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
        if (renderedStats.Level is not (>= 1 and <= 100))
        {
            Abstain("The player advisor supports rendered Miner or Botanist levels 1 through 100.");
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
        if (classJobId is not (MinerBotanistUtilityProfile.MinerClassJobId or MinerBotanistUtilityProfile.BotanistClassJobId))
        {
            Abstain("The current player profile supports rendered Miner and Botanist; other player jobs remain visible but unsupported.");
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

        if (workbenchValidationRequest is { } validationRequest)
        {
            CompleteWorkbenchValidation(validationRequest);
            return;
        }

        offers = catalog.Build(classJobId, checked((uint)baseline.Level!.Value));
        if (offers.MarketItemIds.Count == 0)
        {
            Abstain($"The declared market scope contains no eligible items in this game-data version. {offers.Diagnostic}");
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
        if (solving.IsActive)
        {
            TickSolving();
            return;
        }
        if (discoveryRequest is not null && marketDiscovery.GetLiveState(discoveryRequest) is { } live)
        {
            var retainedAdvice = State.Advice;
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
        var solvingEvidence = currentEvidence ?? evidence;
        var capturedBaseline = baseline!;
        var capturedResolution = resolution!;
        var capturedOffers = offers!;
        var capturedContext = State.Context;
        advicePlayerFingerprint = RenderedPlayerAuthorityFingerprint.Capture(capturedBaseline, capturedResolution);
        pendingCurrentEvidence = currentEvidence;
        var capturedGeneration = sessionGeneration;
        solvingStartedAtUtc = DateTimeOffset.UtcNow;
        Volatile.Write(ref solverProgress, null);
        discoveryTask = null;
        discoveryRequest = null;
        solving.Start(
            sessionGeneration,
            token => advisor.Build(
                capturedBaseline,
                capturedResolution,
                solvingEvidence,
                itemId => capturedOffers.Definitions.TryGetValue(itemId, out var definition) ? [definition] : [],
                capturedContext,
                capturedOffers.VendorOffers,
                token,
                progress => Volatile.Write(ref solverProgress, new(capturedGeneration, progress)),
#if DEBUG
                replay => MinerBotanistSolverReplayFileStore.Write(solverReplayPath, replay)
#else
                null
#endif
                ),
            cancellation!.Token);
        State = State with
        {
            Message = $"Market discovery is complete ({evidence.Coverage.QueriedItemCount:N0}/{evidence.Coverage.CatalogItemCount:N0}); solving the exact frontier off the framework tick. Cancel remains available.",
            Completed = evidence.Coverage.QueriedItemCount,
            Total = evidence.Coverage.CatalogItemCount,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void TickSolving()
    {
        var result = solving.Poll(sessionGeneration);
        if (result.Status == GenerationBoundComputationStatus.Pending)
        {
            var progress = Volatile.Read(ref solverProgress);
            var elapsed = DateTimeOffset.UtcNow - solvingStartedAtUtc;
            State = State with
            {
                Message = progress is { Generation: var generation, Progress: var value } && generation == sessionGeneration
                    ? $"Exact frontier {value.CompletedPositionCount:N0}/{value.TotalPositionCount:N0} through {value.Position}: {value.CandidateStateCount:N0} candidates, {value.RetainedStateCount:N0} exact retained states, {value.ExpandedStateCount:N0} expanded ({elapsed.TotalSeconds:N0}s). Cancel remains available."
                    : $"Preparing the exact frontier off the framework tick ({elapsed.TotalSeconds:N0}s). Cancel remains available.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return;
        }
        if (result.Status is GenerationBoundComputationStatus.None or GenerationBoundComputationStatus.Stale)
            return;
        if (result.Status == GenerationBoundComputationStatus.Cancelled)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor frontier construction was cancelled.");
            return;
        }
        if (result.Status == GenerationBoundComputationStatus.Faulted)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Failed,
                $"Advisor frontier construction failed safely: {result.Exception?.Message ?? "Unknown worker failure."}");
            return;
        }

        var advice = result.Value!;
        var stage = advice.Status == MinerBotanistAdvisorStatus.Complete
            ? MinerBotanistAdvisorSessionStage.Complete
            : MinerBotanistAdvisorSessionStage.Abstained;
        var retainPrevious = advice.Status != MinerBotanistAdvisorStatus.Complete && State.Advice is { Frontier: not null };
        if (advice.Status == MinerBotanistAdvisorStatus.Complete)
            CurrentEvidence = pendingCurrentEvidence;
        State = State with
        {
            Stage = stage,
            Message = retainPrevious
                ? $"Refresh abstained: {advice.Diagnostic} The last valid frontier remains visible."
                : advice.Diagnostic,
            Advice = retainPrevious ? State.Advice : advice,
            AdviceIsRetained = retainPrevious,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        pendingCurrentEvidence = null;
        Volatile.Write(ref solverProgress, null);
        DisposeCancellation();
    }

    private void CompleteWorkbenchValidation(WorkbenchValidationRequest request)
    {
        var reobservedPlayer = RenderedPlayerAuthorityFingerprint.Capture(baseline!, resolution!);
        workbenchValidationRequest = null;
        RequestCharacterUiRestore();
        if (reobservedPlayer != request.CapturedPlayer)
        {
            CurrentEvidence = null;
            advicePlayerFingerprint = null;
            State = State with
            {
                Stage = MinerBotanistAdvisorSessionStage.Abstained,
                Message = "The rendered job, equipment, quality, or gathering-stat tuple changed. The prior frontier is read-only; refresh it before Workbench review.",
                AdviceIsRetained = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            DisposeCancellation();
            return;
        }

        completedWorkbenchValidation = OutfitterWorkbenchPlayerValidation.Create(
            request.Advice,
            request.SelectedSolutionId,
            request.Evidence,
            request.CapturedPlayer,
            reobservedPlayer);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Complete,
            Message = "Rendered player lineage revalidated for Workbench review.",
            AdviceIsRetained = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

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
        sessionGeneration++;
        solving.Invalidate();
        pendingCurrentEvidence = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        Volatile.Write(ref solverProgress, null);
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

    private sealed record WorkbenchValidationRequest(
        MinerBotanistReadOnlyAdvice Advice,
        string SelectedSolutionId,
        OutfitterMarketEvidenceBook Evidence,
        RenderedPlayerAuthorityFingerprint CapturedPlayer);

    private sealed record SolverProgressSnapshot(long Generation, EquipmentExactFrontierProgress Progress);
}
