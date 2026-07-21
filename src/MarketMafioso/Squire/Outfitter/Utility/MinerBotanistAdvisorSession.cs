using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    CapturingPlayer,
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
    AdvisorUtilityContextDescriptor Context,
    MinerBotanistReadOnlyAdvice? Advice,
    bool AdviceIsRetained,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsBusy => Stage is MinerBotanistAdvisorSessionStage.CapturingPlayer or
        MinerBotanistAdvisorSessionStage.DiscoveringMarket;
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
/// Framework-ticked orchestration for the player advisor. One windowless player baseline is
/// captured from PlayerState and equipped inventory on the framework thread; immutable market
/// discovery and exact solving proceed from that frozen generation.
/// </summary>
public sealed class MinerBotanistAdvisorSession : IDisposable
{
    private readonly IPlayerAdvisorBaselineSource baselineSource;
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
    private PlayerAdvisorBaseline? baseline;
    private IAdvisorStatFamily? resolvedFamily;
    private MinerBotanistAdvisorCatalogResult? offers;
    private IReadOnlyList<MinerBotanistOwnedItemEvidence>? ownedItemsEvidence;
    private bool ownedInventoryCoverageComplete;
    private OutfitterMarketEvidenceBook? pendingCurrentEvidence;
    private PlayerAdvisorAuthorityFingerprint? advicePlayerFingerprint;
    private WorkbenchValidationRequest? workbenchValidationRequest;
    private OutfitterWorkbenchPlayerValidation? completedWorkbenchValidation;
    private IReadOnlyList<EquipmentInstanceFingerprint> completedValidationOwnedInstances = [];
    private SolverProgressSnapshot? solverProgress;
    private long sessionGeneration;
    private DateTimeOffset solvingStartedAtUtc;
    private string requestedContextId = GathererAdvisorStatFamily.OrdinaryResourceContext.Id;

    public MinerBotanistAdvisorSession(
        IPlayerAdvisorBaselineSource baselineSource,
        IDataManager dataManager,
        IMarketAcquisitionListingSource listingSource,
        string evidencePath)
    {
        this.baselineSource = baselineSource ?? throw new ArgumentNullException(nameof(baselineSource));
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(listingSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        catalog = new(dataManager);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
#if DEBUG
        solverReplayPath = Path.Combine(Path.GetDirectoryName(evidencePath)!, "outfitter-solver-replay.json");
#endif
        marketDiscovery = new(
            listingSource,
            new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), maxEntries: 4096),
            new OutfitterMarketEvidenceFileStore(evidencePath));
        State = Idle(GathererAdvisorStatFamily.Instance.ProfileDescriptor.DefaultContext);
    }

    public MinerBotanistAdvisorSessionState State { get; private set; }

    public OutfitterMarketEvidenceBook? CurrentEvidence { get; private set; }

    public string Region { get; private set; } = "North America";

    public void Begin(AdvisorUtilityContextDescriptor context, string region)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Superseded by a new advisor refresh.");
        sessionGeneration++;
        var retainedAdvice = string.Equals(requestedContextId, context.Id, StringComparison.Ordinal) &&
            State.Advice is { Frontier: not null }
            ? State.Advice
            : null;
        requestedContextId = context.Id;
        if (retainedAdvice is null)
            CurrentEvidence = null;
        var retainedCoverage = retainedAdvice is null
            ? "Coverage is declared after the active job is captured."
            : State.CoverageLabel;
        cancellation = new();
        ResetPendingCapture();
        State = new(
            MinerBotanistAdvisorSessionStage.CapturingPlayer,
            "Capturing current player stats, equipped items, quality, and materia on the next framework tick.",
            retainedCoverage,
            0,
            null,
            context,
            retainedAdvice,
            retainedAdvice is not null,
            DateTimeOffset.UtcNow);
        Region = string.IsNullOrWhiteSpace(region) ? "North America" : region.Trim();
    }

    public void Tick()
    {
        if (!State.IsBusy)
            return;
        try
        {
            switch (State.Stage)
            {
                case MinerBotanistAdvisorSessionStage.CapturingPlayer:
                    TickPlayerCapture();
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
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
        CurrentEvidence = null;
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Cancelled,
            Message = "The player job or equipped inventory changed; refresh before using Advisor evidence.",
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

        var selected = advice.Frontier!.Pareto.Frontier.SingleOrDefault(value =>
            string.Equals(value.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal));
        if (selected is null)
            return false;
        var requiredOwnedInstances = selected.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) ? offer.Offer.Instance : null)
            .Where(instance => instance is not null && !instance.IsEquipped)
            .Select(instance => instance!.Fingerprint)
            .Distinct(EquipmentInstanceFingerprintComparer.Instance)
            .ToArray();
        workbenchValidationRequest = new(advice, selectedSolutionId, evidence, capturedPlayer, requiredOwnedInstances);
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        cancellation = new();
        baseline = null;
        resolvedFamily = null;
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.CapturingPlayer,
            Message = "Revalidating the current player baseline before Workbench review.",
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

        var latest = baselineSource.Capture();
        var latestInstances = new HashSet<EquipmentInstanceFingerprint>(
            latest.EquipmentSnapshot?.Instances.Select(value => value.Fingerprint) ?? [],
            EquipmentInstanceFingerprintComparer.Instance);
        if (latest.Status != PlayerAdvisorBaselineStatus.Complete ||
            PlayerAdvisorAuthorityFingerprint.Capture(latest) != completedWorkbenchValidation.RecapturedPlayer ||
            completedValidationOwnedInstances.Any(value => !latestInstances.Contains(value)))
        {
            completedWorkbenchValidation = null;
            completedValidationOwnedInstances = [];
            CurrentEvidence = null;
            advicePlayerFingerprint = null;
            State = State with
            {
                Stage = MinerBotanistAdvisorSessionStage.Abstained,
                Message = "The player baseline changed after Workbench validation completed; refresh before staging the solution.",
                AdviceIsRetained = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            validation = null!;
            return false;
        }

        validation = completedWorkbenchValidation;
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        return true;
    }

    private void TickPlayerCapture()
    {
        baseline = baselineSource.Capture();
        State = State with { Message = baseline.Diagnostic, UpdatedAtUtc = DateTimeOffset.UtcNow };
        if (baseline.Status != PlayerAdvisorBaselineStatus.Complete || baseline.ClassJobId is not { } classJobId ||
            baseline.Level is not { } level)
        {
            Abstain(baseline.Diagnostic);
            return;
        }

        resolvedFamily = AdvisorStatFamilies.Resolve(classJobId);
        if (resolvedFamily is null)
        {
            Abstain(AdvisorStatFamilies.UnsupportedDiagnostic(classJobId));
            return;
        }
        State = State with { Context = resolvedFamily.ResolveContext(requestedContextId) };

        if (workbenchValidationRequest is { } validationRequest)
        {
            CompleteWorkbenchValidation(validationRequest);
            return;
        }

        offers = catalog.Build(classJobId, checked((uint)level), resolvedFamily);
        if (offers.MarketItemIds.Count == 0)
        {
            Abstain($"The declared market scope contains no eligible items in this game-data version. {offers.Diagnostic}");
            return;
        }

        ownedInventoryCoverageComplete = ComponentIsComplete(baseline, "armoury") && ComponentIsComplete(baseline, "inventory");
        ownedItemsEvidence = CaptureOwnedItems(baseline);
        var ownershipCoverage = ownedInventoryCoverageComplete
            ? "owned armoury, bag, and saddlebag inventory observed via direct container reads (unmelded items use exact NQ/HQ definitions; relevant melded items block paid nomination)"
            : "owned inventory coverage is partial; observed unmelded items use exact NQ/HQ definitions, but paid nomination is disabled";
        var coverageLabel = offers.CoverageLabel.Replace(
            "owned inventory is not yet observed",
            ownershipCoverage,
            StringComparison.Ordinal);
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
            Message = $"Player baseline captured in one framework tick; discovering exact NQ/HQ listings for {offers.MarketItemIds.Count:N0} scoped items.",
            CoverageLabel = coverageLabel,
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
        var capturedOffers = offers!;
        var capturedFamily = resolvedFamily!;
        var capturedOwnedItems = ownedItemsEvidence;
        var capturedOwnedCoverageComplete = ownedInventoryCoverageComplete;
        var capturedContext = State.Context;
        advicePlayerFingerprint = PlayerAdvisorAuthorityFingerprint.Capture(capturedBaseline);
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
                solvingEvidence,
                itemId => capturedOffers.Definitions.TryGetValue(itemId, out var definition) ? [definition] : [],
                capturedFamily,
                capturedContext.Id,
                capturedOffers.VendorOffers,
                capturedOwnedItems,
                token,
                progress => Volatile.Write(ref solverProgress, new(capturedGeneration, progress)),
#if DEBUG
                replay => AdvisorSolverReplayFileStore.Write(solverReplayPath, replay)
#else
                null
#endif
                , capturedOwnedCoverageComplete),
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
        var recapturedPlayer = PlayerAdvisorAuthorityFingerprint.Capture(baseline!);
        var availableInstances = new HashSet<EquipmentInstanceFingerprint>(
            baseline!.EquipmentSnapshot?.Instances.Select(value => value.Fingerprint) ?? [],
            EquipmentInstanceFingerprintComparer.Instance);
        var ownedAllocationsRemainAvailable = request.RequiredOwnedInstances.All(availableInstances.Contains);
        workbenchValidationRequest = null;
        if (recapturedPlayer != request.CapturedPlayer || !ownedAllocationsRemainAvailable)
        {
            CurrentEvidence = null;
            advicePlayerFingerprint = null;
            State = State with
            {
                Stage = MinerBotanistAdvisorSessionStage.Abstained,
                Message = ownedAllocationsRemainAvailable
                    ? "The player job, level, equipment, quality, materia, or relevant stat totals changed. The prior frontier is read-only; refresh it before Workbench review."
                    : "An owned item selected by the prior frontier is no longer available. Refresh before Workbench review.",
                AdviceIsRetained = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            DisposeCancellation();
            return;
        }

        completedValidationOwnedInstances = request.RequiredOwnedInstances;
        completedWorkbenchValidation = OutfitterWorkbenchPlayerValidation.Create(
            request.Advice,
            request.SelectedSolutionId,
            request.Evidence,
            request.CapturedPlayer,
            recapturedPlayer);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Complete,
            Message = "Current player baseline revalidated for Workbench review.",
            AdviceIsRetained = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void Abstain(string message)
    {
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
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
        State = State with
        {
            Stage = terminalStage,
            Message = message,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void ResetPendingCapture()
    {
        discoveryTask = null;
        discoveryRequest = null;
        baseline = null;
        resolvedFamily = null;
        offers = null;
        ownedItemsEvidence = null;
        ownedInventoryCoverageComplete = false;
        pendingCurrentEvidence = null;
        advicePlayerFingerprint = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
    }

    private void DisposeCancellation()
    {
        cancellation?.Dispose();
        cancellation = null;
    }

    private static IReadOnlyList<MinerBotanistOwnedItemEvidence> CaptureOwnedItems(PlayerAdvisorBaseline baseline) =>
        (baseline.EquipmentSnapshot?.Instances ?? [])
            .Where(instance => !instance.IsEquipped && IsOwnedGearContainer(instance.Fingerprint.Container))
            .Select(instance => new MinerBotanistOwnedItemEvidence(
                instance.Fingerprint.ItemId,
                instance.Fingerprint.IsHighQuality,
                OwnedContainerLabel(instance.Fingerprint.Container),
                instance,
                UtilityIsExact: instance.Fingerprint.MateriaIds.Count == 0))
            .ToArray();

    private static bool ComponentIsComplete(PlayerAdvisorBaseline baseline, string component) =>
        baseline.EquipmentSnapshot?.Diagnostics.Components.Any(value =>
            string.Equals(value.Component, component, StringComparison.Ordinal) &&
            value.Status == Franthropy.Dalamud.Characters.SnapshotComponentStatus.Complete) == true;

    private static bool IsOwnedGearContainer(string container) =>
        container.StartsWith("Armory", StringComparison.Ordinal) ||
        container.StartsWith("Inventory", StringComparison.Ordinal) ||
        container.Contains("SaddleBag", StringComparison.Ordinal);

    private static string OwnedContainerLabel(string container) =>
        container.StartsWith("Armory", StringComparison.Ordinal) ? "Armoury"
            : container.Contains("SaddleBag", StringComparison.Ordinal) ? "Saddlebag"
            : "Inventory";

    private static MinerBotanistAdvisorSessionState Idle(AdvisorUtilityContextDescriptor context) => new(
        MinerBotanistAdvisorSessionStage.Idle,
        "Refresh to capture the active player and build read-only advice without opening Character UI.",
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
        PlayerAdvisorAuthorityFingerprint CapturedPlayer,
        IReadOnlyList<EquipmentInstanceFingerprint> RequiredOwnedInstances);

    private sealed record SolverProgressSnapshot(long Generation, EquipmentExactFrontierProgress Progress);
}
