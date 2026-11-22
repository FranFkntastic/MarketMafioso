using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>The logged-in game state the cross-regional pipeline needs.</summary>
public interface ICrossRegionGameState
{
    CrossRegionCharacter? LoggedInCharacter { get; }
    string? CurrentWorldName { get; }
    /// <summary>Live carried gil, or null when unreadable. Retainer gil is never considered.</summary>
    ulong? CarriedGil { get; }
    bool OtherAutomationActive { get; }
    /// <summary>Live Inventory1..4 carried bags (matching Quartermaster courier scope), or null when not loaded.</summary>
    IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags();
    bool TrySwitchCharacter(CrossRegionCharacter target, out string message);
}

public enum CrossRegionShoppingState
{
    NotStarted,
    Preparing,
    Running,
    Paused,
    Completed,
    Failed,
    Stopped,
}

/// <summary>The Market Acquisition route as driven by the cross-regional pipeline on the shopper.</summary>
public interface ICrossRegionShoppingRoute
{
    CrossRegionShoppingState State { get; }
    string StatusMessage { get; }
    bool CanResumeInPlace { get; }
    event Action<MarketAcquisitionConfirmedPurchase>? ConfirmedPurchase;
    IReadOnlyDictionary<string, uint> CaptureShopperStartingStock(CrossRegionAcquisitionSession session);
    void BeginPrepareAndStart(CrossRegionAcquisitionSession session);
    void Tick();
    bool Pause(out string message);
    bool ResumeInPlace(out string message);
    void Stop();
    bool TryCloseMarketBoard();
}

public sealed record CrossRegionStartRequest(
    CrossRegionCharacter Shopper,
    string TargetRegion,
    int FundGil,
    MarketAcquisitionRequestDocument Document,
    IReadOnlyDictionary<string, uint> PrimaryStockBaselines);

public sealed record CrossRegionReadiness(bool Ready, string? Blocker, QuartermasterCourierStatus? Status)
{
    public QuartermasterCourierRosterEntry? ShopperEntry { get; init; }
    /// <summary>Machine-readable blocker so the UI can offer an inline fix; null for blockers without one.</summary>
    public string? BlockerCode { get; init; }
}

public sealed record CrossRegionActionResult(bool Success, string Message)
{
    public static CrossRegionActionResult Ok(string message) => new(true, message);
    public static CrossRegionActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// Single-cycle cross-regional shopping: fund the shopper through Quartermaster's courier, shop the
/// foreign region on the shopper, then haul exactly what was bought back to the primary.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    public const long MaxCarriedGil = 999_999_999L;
    public const int TravelFeeReserveGil = 5_000;
    public const string BlockerShopperNotPaired = "ShopperNotPaired";
    /// <summary>How long an accepted pairing may take to show up in the carrier's presence before it is reported.</summary>
    private static readonly TimeSpan PairConfirmationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SwitchRetryInterval = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HomeWorldArrivalGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CloseBoardTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BagReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AcceptedRunAppearanceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResumeTransitionTimeout = TimeSpan.FromSeconds(10);
    private const int MaxCharacterSwitchAttempts = 3;
    private const int MaxSubmitAttempts = 5;
    private const string CodeCourierHistoryIndexing = "CourierHistoryIndexing";
    // Quartermaster's request history is still loading: the same submit is retried this often, this many times.
    private static readonly TimeSpan HistoryIndexingRetryInterval = TimeSpan.FromSeconds(2);
    private const int MaxHistoryIndexingRetries = 30;
    private const int MaxRecordedPurchaseKeys = 4096;

    private readonly IQuartermasterCourierClient courier;
    private readonly ICrossRegionGameState game;
    private readonly ICrossRegionShoppingRoute shopping;
    private readonly ICrossRegionAcquisitionStore store;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string, Exception?> log;
    private readonly IQuartermasterRunCustodyClient? custody;
    private CrossRegionAcquisitionSession session;
    private DateTimeOffset nextPollUtc;
    private DateTimeOffset? lastSwitchRequestUtc;
    private DateTimeOffset? fundArrivalWaitStartedUtc;
    private DateTimeOffset? closeStartedUtc;
    private DateTimeOffset? awaitingRunAppearanceSinceUtc;
    private DateTimeOffset? awaitingResumeTransitionUntilUtc;
    private (string State, string? Code, string? Detail)? preResumeFailure;
    private bool prepareIssued;
    private int submitAttempts;
    private int historyIndexingRetries;
    private DateTimeOffset nextSubmitRetryUtc;
    private QuartermasterCourierStatus? cachedStatus;
    private string? cachedStatusError;
    private DateTimeOffset cachedStatusAtUtc = DateTimeOffset.MinValue;
    private bool cachedHasCourier;
    private DateTimeOffset cachedHasCourierAtUtc = DateTimeOffset.MinValue;
    // Pairing runs on a worker thread while Draw reads these on the framework thread.
    private volatile bool pairingInFlight;
    private volatile bool statusInvalidated;
    private volatile string? pairingShopperError;
    private volatile string? pairingShopperKey;
    private long awaitingPairConfirmationUntilTicks;

    public CrossRegionAcquisitionCoordinator(
        IQuartermasterCourierClient courier,
        ICrossRegionGameState game,
        ICrossRegionShoppingRoute shopping,
        ICrossRegionAcquisitionStore store,
        Func<DateTimeOffset>? utcNow = null,
        Action<string, Exception?>? log = null,
        IQuartermasterRunCustodyClient? custody = null)
    {
        this.custody = custody;
        this.courier = courier ?? throw new ArgumentNullException(nameof(courier));
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        this.shopping = shopping ?? throw new ArgumentNullException(nameof(shopping));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.log = log ?? ((_, _) => { });
        session = LoadOrIdle();
        shopping.ConfirmedPurchase += OnConfirmedPurchase;
    }

    public CrossRegionAcquisitionSession Session => session;

    public CrossRegionAcquisitionPhase Phase => session.Phase;

    /// <summary>A session that still owns automation (not idle and not terminal).</summary>
    public bool IsActive => session.IsActive;

    public bool HasSession => session.Phase != CrossRegionAcquisitionPhase.Idle;

    public bool IsLoggedInAsShopper => session.Shopper?.Matches(game.LoggedInCharacter) == true;

    public bool CanHaulNow =>
        session.PurchasedUnits > 0 && IsLoggedInAsShopper &&
        (session.Phase == CrossRegionAcquisitionPhase.Paused ||
         session.Phase == CrossRegionAcquisitionPhase.Stopped && session.InterruptedPhase is CrossRegionAcquisitionPhase.Paused or
             CrossRegionAcquisitionPhase.Shopping or CrossRegionAcquisitionPhase.PreparingShopRoute or CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul);

    public bool CanResume => session.Phase switch
    {
        CrossRegionAcquisitionPhase.Paused => true,
        CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped => session.InterruptedPhase is not null &&
            (InterruptedCourierLeg() is not { } leg ||
             session.CanResumeCourier || session.Phase == CrossRegionAcquisitionPhase.Stopped ||
             RunIdOf(leg) is null),
        CrossRegionAcquisitionPhase.FundingShopper or CrossRegionAcquisitionPhase.HaulingToPrimary or
            CrossRegionAcquisitionPhase.CollectingCountedStock or CrossRegionAcquisitionPhase.StowingDelivered => true,
        _ => false,
    };

    public bool CanStop => session.IsActive;

    public bool CanPause => session.Phase == CrossRegionAcquisitionPhase.Shopping;

    public bool CanClear => session.IsTerminal;

    // ---- Status cache (UI and readiness) ---------------------------------------------------

    public QuartermasterCourierStatus? GetCourierStatus(out string? error, bool force = false)
    {
        var now = utcNow();
        if (statusInvalidated)
        {
            statusInvalidated = false;
            force = true;
        }
        if (force || now - cachedStatusAtUtc >= StatusCacheTtl)
        {
            cachedStatusAtUtc = now;
            if (courier.TryGetCourierStatus(out var status, out var statusError))
            {
                cachedStatus = status;
                cachedStatusError = null;
            }
            else
            {
                cachedStatus = null;
                cachedStatusError = statusError;
            }
        }
        error = cachedStatusError;
        return cachedStatus;
    }

    /// <summary>Capability probe, cached like the status so per-frame UI never spams IPC.</summary>
    public bool HasCrossRegionCourier(bool force = false)
    {
        var now = utcNow();
        if (force || now - cachedHasCourierAtUtc >= StatusCacheTtl)
        {
            cachedHasCourierAtUtc = now;
            try { cachedHasCourier = courier.HasCrossRegionCourier; }
            catch { cachedHasCourier = false; }
        }
        return cachedHasCourier;
    }

    // ---- Inline shopper pairing -------------------------------------------------------------

    /// <summary>True while a pairing request is in flight or its acceptance is still propagating to the carrier.</summary>
    public bool IsPairingShopper => pairingInFlight || utcNow().UtcTicks < Interlocked.Read(ref awaitingPairConfirmationUntilTicks);

    public string? PairingShopperError => pairingShopperError;

    /// <summary>Pairs <paramref name="shopper"/> on the carrier off the framework thread; readiness picks up the result on its next poll.</summary>
    public void BeginPairShopper(CrossRegionCharacter shopper)
    {
        ArgumentNullException.ThrowIfNull(shopper);
        if (IsPairingShopper)
            return;
        pairingInFlight = true;
        pairingShopperError = null;
        pairingShopperKey = PairingKey(shopper);
        var owner = shopper.ToCourier();
        _ = Task.Run(() =>
        {
            try
            {
                if (!courier.TryPairCourierOwner(owner, out var ack, out var error))
                    pairingShopperError = $"Pairing {shopper.CharacterName} failed: {error}";
                else if (ack!.Status != QuartermasterCourierAckStatuses.Accepted)
                    pairingShopperError = $"Quartermaster refused to pair {shopper.CharacterName}: {ack.Message ?? ack.ErrorCode ?? ack.Status}";
                else
                    Interlocked.Exchange(ref awaitingPairConfirmationUntilTicks, (utcNow() + PairConfirmationWindow).UtcTicks);
            }
            catch (Exception ex)
            {
                log($"Pairing {shopper.Label} on the courier carrier failed.", ex);
                pairingShopperError = $"Pairing {shopper.CharacterName} failed: {ex.Message}";
            }
            finally
            {
                statusInvalidated = true;
                pairingInFlight = false;
            }
        });
    }

    private static string PairingKey(CrossRegionCharacter shopper) => $"{shopper.LocalContentId}:{shopper.HomeWorldId}";

    public CrossRegionReadiness EvaluateReadiness(
        CrossRegionCharacter? shopper,
        string targetRegion,
        int fundGil,
        MarketAcquisitionRequestDocument? document = null,
        bool force = false)
    {
        if (pairingShopperKey is { } pairingKey && (shopper is null || PairingKey(shopper) != pairingKey))
            ResetPairingState();
        var readiness = EvaluateReadinessCore(shopper, targetRegion, fundGil, document, force);
        if (readiness.BlockerCode != BlockerShopperNotPaired)
        {
            if (readiness.ShopperEntry is not null || readiness.Ready)
                ResetPairingState();
        }
        else if (!pairingInFlight && pairingShopperError is null &&
                 Interlocked.Read(ref awaitingPairConfirmationUntilTicks) is var until and > 0 && utcNow().UtcTicks >= until)
        {
            Interlocked.Exchange(ref awaitingPairConfirmationUntilTicks, 0);
            pairingShopperError = $"Quartermaster accepted the pairing, but {readiness.Status?.Carrier?.CharacterName ?? "the carrier"} has not confirmed it yet.";
        }
        return readiness;
    }

    private void ResetPairingState()
    {
        if (pairingInFlight)
            return;
        pairingShopperKey = null;
        pairingShopperError = null;
        Interlocked.Exchange(ref awaitingPairConfirmationUntilTicks, 0);
    }

    private CrossRegionReadiness EvaluateReadinessCore(
        CrossRegionCharacter? shopper,
        string targetRegion,
        int fundGil,
        MarketAcquisitionRequestDocument? document,
        bool force)
    {
        if (document?.ExactAcquisitionAuthority is not null)
            return new(false, "External exact-acquisition plans cannot run cross-regionally.", null);
        if (document?.Lines.Any(line => line.ItemId is >= 2 and <= 19) == true)
            return new(false, "Cross-regional shopping does not support shards/crystals (item IDs 2–19) because the courier trades Inventory1–4 only.", null);
        if (!HasCrossRegionCourier(force))
            return new(false, "Quartermaster is not loaded or does not support the cross-region courier.", null);
        var status = GetCourierStatus(out var error, force);
        if (status is null)
            return new(false, $"Quartermaster courier status is unavailable: {error}", null);
        if (status.LoggedIn is not { } primary)
            return new(false, "Log in to the primary character first.", status);
        if (status.Carrier is not { Configured: true } carrier)
            return new(false, "No courier carrier is configured in Quartermaster.", status);
        if (!carrier.Online)
            return new(false, $"Carrier {carrier.CharacterName} is offline.", status);
        if (carrier.CarrierOptIn == false)
            return new(false, $"Enable 'Carry for another character' on {carrier.CharacterName}'s Quartermaster Courier settings.", status);
        if (primary.IsPairedWithCarrier == false)
            return new(false, $"Pair {primary.CharacterName} as an owner on {carrier.CharacterName}'s Quartermaster Courier settings.", status);
        if (!primary.HasMeetingBell)
            return new(false, $"{primary.CharacterName} has no saved meeting bell in Quartermaster.", status);
        if (status.Bridge is not { WorldId: > 0 })
            return new(false, "Quartermaster has not resolved a cross-region bridge.", status);
        if (shopper is null)
            return new(false, $"Select a shopper in {targetRegion}.", status);
        if (shopper.LocalContentId == primary.LocalContentId)
            return new(false, "The shopper must be a different character than the one logged in.", status);
        var entry = status.Roster.FirstOrDefault(row => row.LocalContentId == shopper.LocalContentId && row.HomeWorldId == shopper.HomeWorldId);
        if (entry is null)
            return new(false, $"{shopper.Label} is not in Quartermaster's courier roster.", status);
        if (entry.IsCarrier)
            return new(false, $"{shopper.Label} is the courier carrier and cannot shop.", status) { ShopperEntry = entry };
        if (!RegionMatches(entry.RegionName, targetRegion))
            return new(false, $"{shopper.Label} is not in {targetRegion}.", status) { ShopperEntry = entry };
        if (entry.IsPairedWithCarrier == false)
            return new(false, $"{entry.CharacterName} isn't paired on {carrier.CharacterName} yet (needed for the return haul).", status)
            {
                ShopperEntry = entry,
                BlockerCode = BlockerShopperNotPaired,
            };
        if (status.Run is { } run)
        {
            if (run.Active || run.IsRunning)
                return new(false, "Another courier run is active in Quartermaster.", status) { ShopperEntry = entry };
            // Only genuinely unsettled custody or an unverified pending trade blocks starting a new run;
            // zero-custody failed/interrupted runs are auto-archived by Quartermaster on submit.
            if (run.IsFailure && (run.CustodyUnits > 0 || run.CustodyGil > 0 || !run.CanClear))
                return new(false, $"Quartermaster has unsettled courier custody ({run.State}); resolve it in Quartermaster first.", status) { ShopperEntry = entry };
        }
        if (fundGil < 0)
            return new(false, "Transfer gil cannot be negative.", status) { ShopperEntry = entry };
        // The carrier teleports to the bridge and the primary's bell on the return haul even without a gil transfer.
        if (carrier.CarriedGil < TravelFeeReserveGil)
            return new(false, $"Carrier {carrier.CharacterName} needs at least {TravelFeeReserveGil:N0} gil of its own for teleport fees.", status) { ShopperEntry = entry };
        if (fundGil > 0)
        {
            var carried = game.CarriedGil ?? primary.CarriedGil;
            if (carried < (ulong)fundGil + TravelFeeReserveGil)
                return new(false, $"{primary.CharacterName} carries {carried:N0} gil; {fundGil:N0} + {TravelFeeReserveGil:N0} travel reserve needed (retainer gil is never used).", status) { ShopperEntry = entry };
            if ((long)carrier.CarriedGil + fundGil > MaxCarriedGil)
                return new(false, $"Carrier would exceed the {MaxCarriedGil:N0} gil cap.", status) { ShopperEntry = entry };
        }
        if (game.OtherAutomationActive)
            return new(false, "Another MarketMafioso automation is running.", status) { ShopperEntry = entry };
        return new(true, null, status) { ShopperEntry = entry };
    }

    // ---- Commands ---------------------------------------------------------------------------

    public CrossRegionActionResult Start(CrossRegionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (session.Phase != CrossRegionAcquisitionPhase.Idle)
            return CrossRegionActionResult.Fail("A cross-regional session is still unresolved; finish or clear it first.");
        var readiness = EvaluateReadiness(request.Shopper, request.TargetRegion, request.FundGil, request.Document, force: true);
        if (!readiness.Ready)
            return CrossRegionActionResult.Fail(readiness.Blocker ?? "Cross-regional shopping is not ready.");
        var primary = game.LoggedInCharacter;
        if (primary is null)
            return CrossRegionActionResult.Fail("Log in to the primary character first.");

        var now = utcNow();
        var id = Guid.NewGuid();
        session = new CrossRegionAcquisitionSession
        {
            SessionId = id,
            Primary = primary,
            Shopper = request.Shopper,
            TargetRegion = request.TargetRegion,
            FundGil = request.FundGil,
            PrimaryStockBaselines = new Dictionary<string, uint>(request.PrimaryStockBaselines, StringComparer.Ordinal),
            // Detached copy: the Workbench keeps editing (and rescoping) its own document.
            RequestDocumentSnapshot = request.Document with
            {
                Lines = request.Document.Lines.ToList(),
                SweepDataCenters = request.Document.SweepDataCenters.ToList(),
            },
            StartedAtUtc = now,
        };
        PlanCountedStock(readiness.Status?.Carrier);
        if (request.Document.StowAfterDelivery)
        {
            // Nothing has reached the primary yet (funding moves gil only): what its bags hold now is not this run's.
            session.PrimaryBagBaseline = PrimaryBagSnapshot(game.ReadCarriedBags(), request.Document);
            if (session.PrimaryBagBaseline is null)
                log("StowBaselineUnavailable: the primary's bags could not be read at Start; delivered items will stay in the bags.", null);
        }
        ResetTransientState();
        if (request.FundGil > 0)
        {
            // I1: the deterministic request id is durable before anything is submitted.
            session.FundRequestId = CrossRegionAcquisitionSession.FundRequestIdFor(id);
            SetPhase(CrossRegionAcquisitionPhase.FundingShopper, $"Funding {request.Shopper.Label} with {request.FundGil:N0} gil via courier.");
            SubmitCourier(CourierLeg.Fund);
        }
        else if (request.Shopper.Matches(primary))
            SetPhase(CrossRegionAcquisitionPhase.PreparingShopRoute, "Preparing the shop route.");
        else
            SetPhase(CrossRegionAcquisitionPhase.SwitchingToShopper, $"No gil transfer; switching to {request.Shopper.Label}.");
        return CrossRegionActionResult.Ok(session.LastMessage);
    }

    public CrossRegionActionResult Pause()
    {
        if (session.Phase != CrossRegionAcquisitionPhase.Shopping)
            return CrossRegionActionResult.Fail("Only an active shopping stage can be paused.");
        shopping.Pause(out var message);
        session.ShoppingPausedByUser = true;
        SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage("Shopping paused."));
        return CrossRegionActionResult.Ok(message);
    }

    public CrossRegionActionResult Stop()
    {
        var phase = session.Phase;
        if (session.IsActive)
            CancelOpenCustodyOperations("Stopped from MarketMafioso.");
        switch (phase)
        {
            case CrossRegionAcquisitionPhase.CollectingCountedStock when CurrentCollection()?.State != CrossRegionCollectionState.Shipping:
            case CrossRegionAcquisitionPhase.StowingDelivered:
                // No courier custody here; an open retrieval or stow was withdrawn above. Resume settles it from its status.
                session.InterruptedPhase = phase;
                SetPhase(CrossRegionAcquisitionPhase.Stopped, $"Stopped during {Describe(phase)}.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.FundingShopper:
            case CrossRegionAcquisitionPhase.HaulingToPrimary:
            case CrossRegionAcquisitionPhase.CollectingCountedStock:
            {
                var leg = LegOf(phase)!.Value;
                var runId = RunIdOf(leg);
                if (runId is null && TryFindOwnRun(leg, out var run) && run!.RunId is { } discovered)
                    runId = discovered;
                var detail = "Courier stop requested.";
                if (runId is { } id)
                {
                    if (!courier.TryStopCourierRun(id.ToString("D"), out var ack, out var error))
                        detail = $"Courier stop could not be confirmed: {error}";
                    else if (ack?.Message is { Length: > 0 } ackMessage)
                        detail = ackMessage;
                }
                else
                    detail = "No courier run was confirmed yet; nothing to stop in Quartermaster.";
                session.InterruptedPhase = phase;
                session.CanResumeCourier = runId is not null;
                SetPhase(CrossRegionAcquisitionPhase.Stopped, $"Stopped during {Describe(phase)}. {detail}");
                return CrossRegionActionResult.Ok(session.LastMessage);
            }
            case CrossRegionAcquisitionPhase.Shopping:
            case CrossRegionAcquisitionPhase.PreparingShopRoute:
                shopping.Stop();
                prepareIssued = false;
                session.ShoppingPausedByUser = true;
                SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage("Shopping stopped."));
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.Paused:
            case CrossRegionAcquisitionPhase.SwitchingToShopper:
            case CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul:
            case CrossRegionAcquisitionPhase.ReturningToPrimary:
                if (phase == CrossRegionAcquisitionPhase.Paused)
                    shopping.Stop();
                session.InterruptedPhase = phase;
                SetPhase(CrossRegionAcquisitionPhase.Stopped, $"Stopped during {Describe(phase)}.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            default:
                return CrossRegionActionResult.Fail("Nothing to stop.");
        }
    }

    public CrossRegionActionResult Resume()
    {
        var target = session.Phase is CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped
            ? session.InterruptedPhase
            : session.Phase;
        switch (target)
        {
            case CrossRegionAcquisitionPhase.FundingShopper:
            case CrossRegionAcquisitionPhase.HaulingToPrimary:
                return ResumeCourier(target.Value);
            case CrossRegionAcquisitionPhase.CollectingCountedStock:
                if (CurrentCollection()?.State == CrossRegionCollectionState.Shipping)
                    return ResumeCourier(target.Value);
                session.CharacterSwitchAttempts = 0;
                lastSwitchRequestUtc = null;
                collectBagWaitStartedUtc = null;
                SetPhase(CrossRegionAcquisitionPhase.CollectingCountedStock, CollectingMessage());
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.StowingDelivered:
                session.CharacterSwitchAttempts = 0;
                lastSwitchRequestUtc = null;
                SetPhase(CrossRegionAcquisitionPhase.StowingDelivered, "Stowing delivered items.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.Paused:
            case CrossRegionAcquisitionPhase.Shopping:
            case CrossRegionAcquisitionPhase.PreparingShopRoute:
                return ResumeShopping();
            case CrossRegionAcquisitionPhase.SwitchingToShopper:
                session.CharacterSwitchAttempts = 0;
                lastSwitchRequestUtc = null;
                SetPhase(CrossRegionAcquisitionPhase.SwitchingToShopper, $"Switching to {session.Shopper?.Label}.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul:
                closeStartedUtc = null;
                SetPhase(CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul, "Preparing the return haul.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            case CrossRegionAcquisitionPhase.ReturningToPrimary:
                session.CharacterSwitchAttempts = 0;
                lastSwitchRequestUtc = null;
                closeStartedUtc = null;
                SetPhase(CrossRegionAcquisitionPhase.ReturningToPrimary, $"Returning to {session.Primary?.Label}.");
                return CrossRegionActionResult.Ok(session.LastMessage);
            default:
                return CrossRegionActionResult.Fail("Nothing to resume.");
        }
    }

    public CrossRegionActionResult HaulNow()
    {
        if (!CanHaulNow)
            return CrossRegionActionResult.Fail(session.PurchasedUnits == 0
                ? "Nothing has been purchased yet."
                : !IsLoggedInAsShopper ? $"Log in as {session.Shopper?.Label} to haul." : "Haul is available only while shopping is paused or stopped.");
        BeginHaul("Hauling purchased items now.");
        return CrossRegionActionResult.Ok(session.LastMessage);
    }

    public CrossRegionActionResult Clear()
    {
        if (!session.IsTerminal)
            return CrossRegionActionResult.Fail("Only a completed, failed, or stopped session can be cleared.");
        CancelOpenCustodyOperations("Cleared from MarketMafioso.");
        try { store.Clear(); }
        catch (Exception ex) { log("Cross-regional session file could not be removed.", ex); }
        session = new CrossRegionAcquisitionSession { Phase = CrossRegionAcquisitionPhase.Idle };
        ResetTransientState();
        return CrossRegionActionResult.Ok("Cross-regional session cleared.");
    }

    // ---- Tick -------------------------------------------------------------------------------

    public void Tick()
    {
        if (!session.IsActive)
            return;
        try { shopping.Tick(); }
        catch (Exception ex) { log("Cross-regional shop route tick failed.", ex); }
        var now = utcNow();
        if (now < nextPollUtc)
            return;
        nextPollUtc = now + PollInterval;
        try
        {
            switch (session.Phase)
            {
                case CrossRegionAcquisitionPhase.FundingShopper:
                    TickCourier(CourierLeg.Fund, now);
                    break;
                case CrossRegionAcquisitionPhase.SwitchingToShopper:
                    TickSwitch(now);
                    break;
                case CrossRegionAcquisitionPhase.PreparingShopRoute:
                    TickPreparing();
                    break;
                case CrossRegionAcquisitionPhase.Shopping:
                    TickShopping();
                    break;
                case CrossRegionAcquisitionPhase.Paused:
                    TickPaused();
                    break;
                case CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul:
                    TickClosing(now);
                    break;
                case CrossRegionAcquisitionPhase.HaulingToPrimary:
                    TickCourier(CourierLeg.Haul, now);
                    break;
                case CrossRegionAcquisitionPhase.ReturningToPrimary:
                    TickReturning(now);
                    break;
                case CrossRegionAcquisitionPhase.CollectingCountedStock:
                    TickCollecting(now);
                    break;
                case CrossRegionAcquisitionPhase.StowingDelivered:
                    TickStowing(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            log($"Cross-regional {Describe(session.Phase)} tick failed.", ex);
            session.LastMessage = $"{Describe(session.Phase)}: {ex.Message}";
        }
    }

    private void TickCourier(CourierLeg leg, DateTimeOffset now)
    {
        var fund = leg == CourierLeg.Fund;
        if (fund && session.FundCompleted)
        {
            AdvanceAfterFund(now);
            return;
        }
        if (!courier.TryGetCourierStatus(out var status, out var error))
        {
            session.LastMessage = $"Waiting for Quartermaster courier status: {error}";
            return;
        }
        var run = MatchOwnRun(status!, leg);
        if (run is null)
        {
            var runId = RunIdOf(leg);
            if (runId is null)
            {
                awaitingRunAppearanceSinceUtc = null;
                if (now >= nextSubmitRetryUtc)
                    SubmitCourier(leg);
            }
            else
            {
                awaitingRunAppearanceSinceUtc ??= now;
                if (now - awaitingRunAppearanceSinceUtc.Value >= AcceptedRunAppearanceTimeout)
                {
                    awaitingRunAppearanceSinceUtc = null;
                    Fail(session.Phase, "CourierStartNotObserved",
                        $"Quartermaster accepted courier run {runId} but did not report it within {AcceptedRunAppearanceTimeout.TotalSeconds:N0}s; check Quartermaster.",
                        canResumeCourier: false);
                }
                else
                    session.LastMessage = $"Waiting for Quartermaster to report courier run {runId}.";
            }
            return;
        }
        awaitingRunAppearanceSinceUtc = null;
        if (run.RunId is { } id && RunIdOf(leg) != id)
        {
            SetRunId(leg, id);
            Save();
        }

        if (run.IsRunning || run.Active)
        {
            awaitingResumeTransitionUntilUtc = null;
            preResumeFailure = null;
            var summary = FormatCourierPhase(run);
            if (!string.Equals(summary, session.CourierPhaseSummary, StringComparison.Ordinal))
            {
                session.CourierPhaseSummary = summary;
                session.LastMessage = $"{DescribeTitle(session.Phase)}: {summary}";
                Save();
            }
            return;
        }
        if (run.IsCompleted)
        {
            awaitingResumeTransitionUntilUtc = null;
            preResumeFailure = null;
            session.CourierPhaseSummary = null;
            if (fund)
            {
                session.FundCompleted = true;
                session.LastMessage = $"{session.FundGil:N0} gil delivered to {session.Shopper?.Label}.";
                Save();
                AdvanceAfterFund(now);
            }
            else if (leg == CourierLeg.Collect)
                OnCollectShipped();
            else
                AfterDelivery(HaulSummary());
            return;
        }
        if (run.IsFailure)
        {
            // Quartermaster starts Resume() asynchronously; ignore the identical pre-resume failure briefly
            // until QM transitions to Active/Running or settles into a new outcome.
            if (awaitingResumeTransitionUntilUtc is { } until &&
                now < until &&
                preResumeFailure == (run.State, run.Code, run.Detail))
            {
                session.LastMessage = $"Waiting for Quartermaster to resume {LegNoun(leg)}…";
                return;
            }
            awaitingResumeTransitionUntilUtc = null;
            preResumeFailure = null;
            // Zero counters alone do not prove settled custody; only a clearable run may be skipped.
            if (leg == CourierLeg.Collect && run.CanClear && run.CustodyUnits == 0 && run.CustodyGil == 0)
            {
                OnCollectFailedWithoutCustody(run);
                return;
            }
            Fail(session.Phase, run.Code ?? run.State,
                $"Courier {LegNoun(leg)} {run.State.ToLowerInvariant()}: {run.Detail ?? run.Code ?? "no detail"}",
                run.CanResume);
        }
    }

    private void AdvanceAfterFund(DateTimeOffset now)
    {
        var me = game.LoggedInCharacter;
        if (me is null)
        {
            session.LastMessage = "Gil delivered; waiting for a logged-in character.";
            return;
        }
        if (!session.Shopper!.Matches(me))
        {
            fundArrivalWaitStartedUtc = null;
            SetPhase(CrossRegionAcquisitionPhase.SwitchingToShopper, $"Gil delivered; switching to {session.Shopper.Label}.");
            return;
        }
        var onHome = string.Equals(game.CurrentWorldName, session.Shopper.HomeWorldName, StringComparison.OrdinalIgnoreCase);
        fundArrivalWaitStartedUtc ??= now;
        if (!onHome && now - fundArrivalWaitStartedUtc.Value < HomeWorldArrivalGrace)
        {
            session.LastMessage = $"Gil delivered; waiting for {session.Shopper.CharacterName} to reach {session.Shopper.HomeWorldName}.";
            return;
        }
        fundArrivalWaitStartedUtc = null;
        SetPhase(CrossRegionAcquisitionPhase.PreparingShopRoute, $"Preparing the {session.TargetRegion} shop route on {session.Shopper.Label}.");
    }

    private void TickSwitch(DateTimeOffset now)
    {
        var shopper = session.Shopper!;
        var me = game.LoggedInCharacter;
        if (shopper.Matches(me))
        {
            lastSwitchRequestUtc = null;
            session.CharacterSwitchAttempts = 0;
            SetPhase(CrossRegionAcquisitionPhase.PreparingShopRoute, $"Preparing the {session.TargetRegion} shop route on {shopper.Label}.");
            return;
        }
        DriveCharacterSwitch(shopper, CrossRegionAcquisitionPhase.SwitchingToShopper, "ShopperSwitchFailed", now);
    }

    /// <summary>
    /// Requests a character switch with bounded retries; the caller detects arrival. When attempts run out the
    /// run fails, unless <paramref name="onExhausted"/> settles the step instead.
    /// </summary>
    private void DriveCharacterSwitch(CrossRegionCharacter target, CrossRegionAcquisitionPhase phase, string failureCode, DateTimeOffset now,
        Action<string>? onExhausted = null)
    {
        if (lastSwitchRequestUtc is { } last && now - last < SwitchRetryInterval)
            return;
        if (game.LoggedInCharacter is null && lastSwitchRequestUtc is not null)
            return;
        if (session.CharacterSwitchAttempts >= MaxCharacterSwitchAttempts)
        {
            var detail = $"Could not switch to {target.Label} after {MaxCharacterSwitchAttempts} attempts.";
            if (onExhausted is not null)
                onExhausted(detail);
            else
                Fail(phase, failureCode, detail, canResumeCourier: false);
            return;
        }
        session.CharacterSwitchAttempts++;
        lastSwitchRequestUtc = now;
        session.LastMessage = game.TrySwitchCharacter(target, out var message)
            ? $"Switching to {target.Label}…"
            : $"Character switch to {target.Label} was not accepted: {message}";
        Save();
    }

    /// <summary>Nothing to haul: leave the shopper (market board closed first) and finish on the primary.</summary>
    private void ReturnToPrimaryOrComplete(string reason)
    {
        if (session.Primary is not { } primary || primary.Matches(game.LoggedInCharacter))
        {
            AfterDelivery($"{reason}.");
            return;
        }
        session.CharacterSwitchAttempts = 0;
        lastSwitchRequestUtc = null;
        closeStartedUtc = null;
        SetPhase(CrossRegionAcquisitionPhase.ReturningToPrimary, $"{reason}; returning to {primary.Label}.");
    }

    private void TickReturning(DateTimeOffset now)
    {
        var primary = session.Primary!;
        if (primary.Matches(game.LoggedInCharacter))
        {
            lastSwitchRequestUtc = null;
            session.CharacterSwitchAttempts = 0;
            AfterDelivery(session.PurchasedUnits == 0
                ? "Shopping finished with no purchases; returned to primary."
                : "None of the purchased items remained in bags; returned to primary.");
            return;
        }
        if (session.Shopper?.Matches(game.LoggedInCharacter) == true)
        {
            closeStartedUtc ??= now;
            if (shopping.State is CrossRegionShoppingState.Running or CrossRegionShoppingState.Paused or CrossRegionShoppingState.Preparing)
                shopping.Stop();
            if (!shopping.TryCloseMarketBoard() && now - closeStartedUtc.Value < CloseBoardTimeout)
            {
                session.LastMessage = "Closing the market board before returning to the primary.";
                return;
            }
        }
        DriveCharacterSwitch(primary, CrossRegionAcquisitionPhase.ReturningToPrimary, "PrimarySwitchFailed", now);
    }

    private void TickPreparing()
    {
        var shopper = session.Shopper!;
        if (!shopper.Matches(game.LoggedInCharacter))
        {
            SetPhase(CrossRegionAcquisitionPhase.SwitchingToShopper, $"Switching to {shopper.Label} to shop.");
            return;
        }
        if (!prepareIssued)
        {
            if (session.ShopperStartingStock is null)
            {
                session.ShopperStartingStock = new Dictionary<string, uint>(shopping.CaptureShopperStartingStock(session), StringComparer.Ordinal);
                Save();
            }
            prepareIssued = true;
            shopping.BeginPrepareAndStart(session);
            session.LastMessage = $"Preparing the {session.TargetRegion} shop route on {shopper.Label}.";
            return;
        }
        switch (shopping.State)
        {
            case CrossRegionShoppingState.Running:
                session.ShoppingPausedByUser = false;
                SetPhase(CrossRegionAcquisitionPhase.Shopping, $"Shopping {session.TargetRegion} on {shopper.Label}.");
                break;
            case CrossRegionShoppingState.Completed:
                FinishShopping();
                break;
            case CrossRegionShoppingState.Failed:
            case CrossRegionShoppingState.Stopped:
                prepareIssued = false;
                SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage($"Shop route could not start: {shopping.StatusMessage}"));
                break;
            default:
                session.LastMessage = shopping.StatusMessage;
                break;
        }
    }

    private void TickShopping()
    {
        if (!session.Shopper!.Matches(game.LoggedInCharacter))
        {
            SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage("Shopper is no longer logged in; shopping paused."));
            return;
        }
        switch (shopping.State)
        {
            case CrossRegionShoppingState.Running:
            case CrossRegionShoppingState.Preparing:
                session.LastMessage = shopping.StatusMessage;
                break;
            case CrossRegionShoppingState.Completed:
                FinishShopping();
                break;
            case CrossRegionShoppingState.Paused:
                SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage($"Route paused: {shopping.StatusMessage}"));
                break;
            default:
                prepareIssued = false;
                SetPhase(CrossRegionAcquisitionPhase.Paused, PausedMessage($"Route ended without completing: {shopping.StatusMessage}"));
                break;
        }
    }

    private void TickPaused()
    {
        // The ordinary route controls can resume the engine directly; follow it back into Shopping.
        if (session.Shopper!.Matches(game.LoggedInCharacter) && shopping.State == CrossRegionShoppingState.Running)
        {
            session.ShoppingPausedByUser = false;
            SetPhase(CrossRegionAcquisitionPhase.Shopping, $"Shopping {session.TargetRegion} on {session.Shopper.Label}.");
        }
        else if (shopping.State == CrossRegionShoppingState.Completed && prepareIssued)
            FinishShopping();
    }

    private void FinishShopping()
    {
        prepareIssued = false;
        if (session.PurchasedUnits == 0 && !HasShopperCountedStock)
        {
            ReturnToPrimaryOrComplete("Shopping finished with no purchases");
            return;
        }
        BeginHaul(session.PurchasedUnits == 0
            ? "Shopping finished with no purchases. Preparing the return haul of counted stock."
            : $"Shopping finished: {session.PurchasedUnits:N0} item(s) for {session.TotalSpentGil:N0} gil. Preparing the return haul.");
    }

    private void BeginHaul(string message)
    {
        closeStartedUtc = null;
        SetPhase(CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul, message);
    }

    private void TickClosing(DateTimeOffset now)
    {
        var shopper = session.Shopper!;
        if (!shopper.Matches(game.LoggedInCharacter))
        {
            session.LastMessage = $"Log in as {shopper.Label} to haul the purchases.";
            return;
        }
        closeStartedUtc ??= now;
        if (shopping.State is CrossRegionShoppingState.Running or CrossRegionShoppingState.Paused or CrossRegionShoppingState.Preparing)
            shopping.Stop();
        if (!shopping.TryCloseMarketBoard() && now - closeStartedUtc.Value < CloseBoardTimeout)
        {
            session.LastMessage = "Closing the market board before the return haul.";
            return;
        }
        if (session.ShopperCounted is { } shopperCounted && !shopperCounted.RetrievalSettled)
        {
            TickRetrieval(shopperCounted, -1, now);
            // The bag-read window starts once the retrieval settles, not when the board closed.
            if (shopperCounted.RetrievalSettled)
                closeStartedUtc = now;
            return;
        }
        var bags = game.ReadCarriedBags();
        if (bags is null)
        {
            if (now - closeStartedUtc.Value >= BagReadTimeout)
                Fail(CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul, "BagsUnavailable",
                    $"Could not read {shopper.CharacterName}'s bags to reconcile the haul.", canResumeCourier: false);
            else
                session.LastMessage = "Waiting for bags to load before reconciling the haul.";
            return;
        }

        var manifest = AddShopperCountedStock(ReconcileManifest(session.PurchasedManifest, bags), bags);
        if (manifest.Count == 0)
        {
            ReturnToPrimaryOrComplete($"None of the purchased items remain in {shopper.CharacterName}'s bags");
            return;
        }
        // I1: persist the haul request before it is submitted.
        session.HaulAttempt++;
        session.HaulRequestId = CrossRegionAcquisitionSession.HaulRequestIdFor(session.SessionId, session.HaulAttempt);
        session.HaulRunId = null;
        session.HaulManifest = manifest.ToList();
        submitAttempts = 0;
        SetPhase(CrossRegionAcquisitionPhase.HaulingToPrimary,
            $"Hauling {manifest.Sum(line => (long)line.Quantity):N0} item(s) back to {session.Primary?.Label}.");
        SubmitCourier(CourierLeg.Haul);
    }

    private CrossRegionActionResult ResumeCourier(CrossRegionAcquisitionPhase target)
    {
        var leg = LegOf(target)!.Value;
        var fund = leg == CourierLeg.Fund;
        if (fund && session.FundCompleted)
        {
            SetPhase(CrossRegionAcquisitionPhase.FundingShopper, "Gil already delivered; continuing.");
            return CrossRegionActionResult.Ok(session.LastMessage);
        }
        if (!courier.TryGetCourierStatus(out var status, out var error))
            return CrossRegionActionResult.Fail($"Quartermaster courier status is unavailable: {error}");
        var run = MatchOwnRun(status!, leg);
        var runId = RunIdOf(leg);
        if (run is null)
        {
            if (runId is not null)
                return CrossRegionActionResult.Fail("Quartermaster no longer reports this courier run; resolve it in Quartermaster.");
            // Never acknowledged: the next tick resubmits under the same deterministic request id.
            SetPhase(target, $"Resuming {Describe(target)}.");
            return CrossRegionActionResult.Ok(session.LastMessage);
        }
        if (run.IsRunning || run.IsCompleted)
        {
            session.CanResumeCourier = false;
            SetPhase(target, $"Resuming {Describe(target)}.");
            return CrossRegionActionResult.Ok(session.LastMessage);
        }
        if (!run.CanResume)
            return CrossRegionActionResult.Fail($"Quartermaster cannot resume this courier run ({run.Code ?? run.State}): {run.Detail}");
        var resumeId = (run.RunId ?? runId)?.ToString("D");
        if (resumeId is null)
            return CrossRegionActionResult.Fail("Quartermaster did not report a run id to resume.");
        if (!courier.TryResumeCourierRun(resumeId, out var ack, out error))
            return CrossRegionActionResult.Fail($"Courier resume failed: {error}");
        if (ack?.Status == QuartermasterCourierAckStatuses.Stopping)
            return CrossRegionActionResult.Fail(ack.Message ?? "Quartermaster is still finishing cleanup; retry in a moment.");
        if (ack is { IsAccepted: false } && ack.Status == QuartermasterCourierAckStatuses.Rejected)
            return CrossRegionActionResult.Fail($"Quartermaster refused to resume: {ack.Message ?? ack.ErrorCode}");
        if (run.RunId is { } id)
            SetRunId(leg, id);
        session.CanResumeCourier = false;
        session.LastErrorCode = null;
        awaitingResumeTransitionUntilUtc = utcNow() + ResumeTransitionTimeout;
        preResumeFailure = (run.State, run.Code, run.Detail);
        SetPhase(target, $"Resumed courier {LegNoun(leg)}.");
        return CrossRegionActionResult.Ok(session.LastMessage);
    }

    private CrossRegionActionResult ResumeShopping()
    {
        if (!IsLoggedInAsShopper)
            return CrossRegionActionResult.Fail($"Log in as {session.Shopper?.Label} to resume shopping.");
        session.ShoppingPausedByUser = false;
        if (shopping.CanResumeInPlace && shopping.ResumeInPlace(out var message))
        {
            SetPhase(CrossRegionAcquisitionPhase.Shopping, message);
            return CrossRegionActionResult.Ok(message);
        }
        prepareIssued = false;
        SetPhase(CrossRegionAcquisitionPhase.PreparingShopRoute, "Re-preparing the remaining shop route.");
        return CrossRegionActionResult.Ok(session.LastMessage);
    }

    // ---- Courier submission -----------------------------------------------------------------

    private void SubmitCourier(CourierLeg leg)
    {
        var requestId = RequestIdOf(leg);
        var collection = leg == CourierLeg.Collect ? CurrentCollection() : null;
        if (string.IsNullOrWhiteSpace(requestId) || session.Primary is null || session.Shopper is null ||
            leg == CourierLeg.Collect && collection is null)
        {
            Fail(session.Phase, "MissingRequest", "Courier request identity was not persisted.", canResumeCourier: false);
            return;
        }
        var request = leg switch
        {
            CourierLeg.Fund => new QuartermasterCourierTransferRequest(requestId, session.Primary.ToCourier(), session.Shopper.ToCourier(),
                session.FundGil, [], ReturnOwner: false, ReturnDestinationHome: true, ReturnCarrier: true),
            CourierLeg.Haul => new QuartermasterCourierTransferRequest(requestId, session.Shopper.ToCourier(), session.Primary.ToCourier(), 0,
                ToCourierItems(session.HaulManifest), ReturnOwner: false, ReturnDestinationHome: false, ReturnCarrier: true),
            // Ends on the primary (the destination) at its home world, ready for the next source or the stow.
            _ => new QuartermasterCourierTransferRequest(requestId, collection!.Source.ToCourier(), session.Primary.ToCourier(), 0,
                ToCourierItems(collection.Manifest), ReturnOwner: false, ReturnDestinationHome: true, ReturnCarrier: true),
        };
        if (!courier.TrySubmitCourierTransfer(request, out var ack, out var error))
        {
            submitAttempts++;
            if (submitAttempts >= MaxSubmitAttempts)
            {
                Fail(session.Phase, "CourierSubmitFailed", $"Courier submission did not succeed: {error}", canResumeCourier: false);
                return;
            }
            session.LastMessage = $"Courier submission pending ({submitAttempts}/{MaxSubmitAttempts}): {error}";
            Save();
            return;
        }
        submitAttempts = 0;
        if (!ack!.IsAccepted)
        {
            if (ack.Status == QuartermasterCourierAckStatuses.Stopping)
            {
                session.LastMessage = "Quartermaster is stopping a previous run; retrying shortly.";
                return;
            }
            if (ack.ErrorCode == CodeCourierHistoryIndexing)
            {
                // Nothing started: the same request id is sent again shortly. Exhaustion stops the run with the
                // request still unsent, so Resume retries it under the same id; nothing is settled or skipped.
                if (++historyIndexingRetries > MaxHistoryIndexingRetries)
                {
                    historyIndexingRetries = 0;
                    Fail(session.Phase, CodeCourierHistoryIndexing,
                        $"Quartermaster's courier history was still loading after {MaxHistoryIndexingRetries} retries; resume to send {requestId} again.",
                        canResumeCourier: false);
                    return;
                }
                nextSubmitRetryUtc = utcNow() + HistoryIndexingRetryInterval;
                session.LastMessage = $"Quartermaster is loading its courier history; retrying ({historyIndexingRetries}/{MaxHistoryIndexingRetries}).";
                return;
            }
            if (leg == CourierLeg.Collect)
            {
                // Rejected before any custody: this source is not collected and the run moves on.
                OnCollectRejected(ack.ErrorCode ?? ack.Status, ack.Message ?? ack.ErrorCode ?? ack.Status);
                return;
            }
            Fail(session.Phase, ack.ErrorCode ?? ack.Status, $"Quartermaster rejected the courier request: {ack.Message ?? ack.ErrorCode ?? ack.Status}",
                canResumeCourier: false);
            return;
        }
        if (ack.RunId is { } runId)
            SetRunId(leg, runId);
        session.LastMessage = leg switch
        {
            CourierLeg.Fund => $"Courier accepted the {session.FundGil:N0} gil transfer to {session.Shopper.Label}.",
            CourierLeg.Haul => $"Courier accepted the return haul to {session.Primary.Label}.",
            _ => $"Courier accepted the collection from {collection!.Source.Label}.",
        };
        Save();
    }

    private QuartermasterCourierRun? MatchOwnRun(QuartermasterCourierStatus status, CourierLeg leg)
    {
        var run = status.Run;
        if (run is null)
            return null;
        var runId = RunIdOf(leg);
        var requestId = RequestIdOf(leg);
        return runId is { } id && run.RunId == id ||
               !string.IsNullOrWhiteSpace(requestId) && string.Equals(run.RequestId, requestId, StringComparison.Ordinal)
            ? run
            : null;
    }

    private bool TryFindOwnRun(CourierLeg leg, out QuartermasterCourierRun? run)
    {
        run = courier.TryGetCourierStatus(out var status, out _) ? MatchOwnRun(status!, leg) : null;
        return run is not null;
    }

    // ---- Purchases --------------------------------------------------------------------------

    private void OnConfirmedPurchase(MarketAcquisitionConfirmedPurchase purchase)
    {
        if (session.Phase is not (CrossRegionAcquisitionPhase.Shopping or CrossRegionAcquisitionPhase.PreparingShopRoute or
            CrossRegionAcquisitionPhase.Paused or CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul))
            return;
        if (purchase.ItemId == 0 || purchase.Quantity == 0)
            return;
        var key = $"{purchase.WorldName}:{purchase.ListingId}";
        if (!string.IsNullOrWhiteSpace(purchase.ListingId))
        {
            if (session.RecordedPurchaseKeys.Contains(key, StringComparer.Ordinal))
                return;
            session.RecordedPurchaseKeys.Add(key);
            if (session.RecordedPurchaseKeys.Count > MaxRecordedPurchaseKeys)
                session.RecordedPurchaseKeys.RemoveRange(0, session.RecordedPurchaseKeys.Count - MaxRecordedPurchaseKeys);
        }
        var index = session.PurchasedManifest.FindIndex(line => line.ItemId == purchase.ItemId && line.IsHq == purchase.IsHq);
        if (index < 0)
            session.PurchasedManifest.Add(new CrossRegionManifestLine(purchase.ItemId, purchase.ItemName ?? purchase.ItemId.ToString(), purchase.IsHq,
                purchase.Quantity, purchase.WalletGil));
        else
        {
            var line = session.PurchasedManifest[index];
            session.PurchasedManifest[index] = line with
            {
                Quantity = checked(line.Quantity + purchase.Quantity),
                SpentGil = checked(line.SpentGil + purchase.WalletGil),
                ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? purchase.ItemName ?? line.ItemName : line.ItemName,
            };
        }
        session.TotalSpentGil = checked(session.TotalSpentGil + purchase.WalletGil);
        Save();
    }

    /// <summary>
    /// I7: the haul ships only what was bought and is still carried. Each (item, quality) is capped
    /// to the live bag quantity; anything else in the shopper's bags is never included.
    /// </summary>
    public static IReadOnlyList<CrossRegionManifestLine> ReconcileManifest(
        IEnumerable<CrossRegionManifestLine> purchased,
        IEnumerable<CrossRegionBagStack> liveBags)
    {
        var carried = liveBags
            .GroupBy(stack => (stack.ItemId, stack.IsHq))
            .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, stack) => total + stack.Quantity));
        return purchased
            .GroupBy(line => (line.ItemId, line.IsHq))
            .Select(group =>
            {
                var bought = group.Aggregate(0UL, (total, line) => total + line.Quantity);
                var live = carried.GetValueOrDefault(group.Key);
                return new CrossRegionManifestLine(group.Key.ItemId, group.First().ItemName, group.Key.IsHq,
                    (uint)Math.Min(Math.Min(bought, live), uint.MaxValue),
                    group.Aggregate(0UL, (total, line) => total + line.SpentGil));
            })
            .Where(line => line.Quantity > 0)
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToArray();
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private void SetPhase(CrossRegionAcquisitionPhase phase, string message)
    {
        session.Phase = phase;
        session.LastMessage = message;
        if (phase is not (CrossRegionAcquisitionPhase.FundingShopper or CrossRegionAcquisitionPhase.HaulingToPrimary or
            CrossRegionAcquisitionPhase.CollectingCountedStock))
            session.CourierPhaseSummary = null;
        Save();
    }

    private void Fail(CrossRegionAcquisitionPhase phase, string code, string message, bool canResumeCourier)
    {
        session.InterruptedPhase = phase is CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped
            ? session.InterruptedPhase
            : phase;
        session.LastErrorCode = code;
        session.CanResumeCourier = canResumeCourier;
        SetPhase(CrossRegionAcquisitionPhase.Failed, message);
    }

    private void Complete(string message)
    {
        session.CompletedAtUtc = utcNow();
        session.LastErrorCode = null;
        SetPhase(CrossRegionAcquisitionPhase.Completed, message);
    }

    private string HaulSummary()
    {
        var returned = session.HaulManifest.Aggregate(0UL, (total, line) => total + line.Quantity);
        return $"Done: funded {session.FundGil:N0} gil, bought {session.PurchasedUnits:N0} item(s) for {session.TotalSpentGil:N0} gil, " +
               $"returned {returned:N0} item(s) to {session.Primary?.Label}.";
    }

    private string PausedMessage(string reason) =>
        session.PurchasedUnits > 0
            ? $"{reason} {session.PurchasedUnits:N0} item(s) bought so far; resume shopping or haul them now."
            : reason;

    private void Save()
    {
        session.UpdatedAtUtc = utcNow();
        try { store.Save(session); }
        catch (Exception ex)
        {
            log("Cross-regional session could not be saved.", ex);
            session.LastMessage = $"{session.LastMessage} (session save failed: {ex.Message})";
        }
    }

    private CrossRegionAcquisitionSession LoadOrIdle()
    {
        try
        {
            if (store.Load() is { } loaded)
                return loaded;
        }
        catch (Exception ex)
        {
            log("Cross-regional session could not be loaded; starting idle.", ex);
        }
        return new CrossRegionAcquisitionSession { Phase = CrossRegionAcquisitionPhase.Idle };
    }

    private void ResetTransientState()
    {
        nextPollUtc = DateTimeOffset.MinValue;
        lastSwitchRequestUtc = null;
        fundArrivalWaitStartedUtc = null;
        closeStartedUtc = null;
        awaitingRunAppearanceSinceUtc = null;
        awaitingResumeTransitionUntilUtc = null;
        preResumeFailure = null;
        prepareIssued = false;
        submitAttempts = 0;
        historyIndexingRetries = 0;
        nextSubmitRetryUtc = DateTimeOffset.MinValue;
        collectBagWaitStartedUtc = null;
        nextCustodyPollUtc = DateTimeOffset.MinValue;
    }

    public static string FormatCourierPhase(QuartermasterCourierRun run)
    {
        var owner = string.IsNullOrWhiteSpace(run.OwnerPhase) ? run.State : run.OwnerPhase;
        var text = string.IsNullOrWhiteSpace(run.CarrierPhase) ? owner! : $"{owner} · Carrier: {run.CarrierPhase}";
        return run.Trips > 1 ? $"{text} (trip {run.Trip}/{run.Trips})" : text;
    }

    public static string Describe(CrossRegionAcquisitionPhase? phase) => phase switch
    {
        CrossRegionAcquisitionPhase.FundingShopper => "funding the shopper",
        CrossRegionAcquisitionPhase.SwitchingToShopper => "switching to the shopper",
        CrossRegionAcquisitionPhase.PreparingShopRoute => "preparing the shop route",
        CrossRegionAcquisitionPhase.Shopping => "shopping",
        CrossRegionAcquisitionPhase.Paused => "paused shopping",
        CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul => "preparing the return haul",
        CrossRegionAcquisitionPhase.HaulingToPrimary => "the return haul",
        CrossRegionAcquisitionPhase.ReturningToPrimary => "returning to the primary",
        CrossRegionAcquisitionPhase.CollectingCountedStock => "collecting counted stock",
        CrossRegionAcquisitionPhase.StowingDelivered => "stowing delivered items",
        _ => phase?.ToString() ?? "the session",
    };

    /// <summary>Title-case phase label for status headers.</summary>
    public static string DescribeTitle(CrossRegionAcquisitionPhase phase) => phase switch
    {
        CrossRegionAcquisitionPhase.Idle => "Idle",
        CrossRegionAcquisitionPhase.FundingShopper => "Funding shopper",
        CrossRegionAcquisitionPhase.SwitchingToShopper => "Switching to shopper",
        CrossRegionAcquisitionPhase.PreparingShopRoute => "Preparing shop route",
        CrossRegionAcquisitionPhase.Shopping => "Shopping",
        CrossRegionAcquisitionPhase.Paused => "Shopping paused",
        CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul => "Preparing return haul",
        CrossRegionAcquisitionPhase.HaulingToPrimary => "Hauling to primary",
        CrossRegionAcquisitionPhase.ReturningToPrimary => "Returning to primary",
        CrossRegionAcquisitionPhase.CollectingCountedStock => "Collecting counted stock",
        CrossRegionAcquisitionPhase.StowingDelivered => "Stowing delivered items",
        CrossRegionAcquisitionPhase.Completed => "Completed",
        CrossRegionAcquisitionPhase.Stopped => "Stopped",
        CrossRegionAcquisitionPhase.Failed => "Failed",
        _ => phase.ToString(),
    };

    public static bool RegionMatches(string? a, string? b)
    {
        static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "na" or "north america" or "north-america" or "northamerica" => "north america",
            "eu" or "europe" => "europe",
            "jp" or "ja" or "japan" => "japan",
            "oc" or "oce" or "oceania" => "oceania",
            var other => other,
        };
        var left = Normalize(a);
        return left.Length > 0 && left == Normalize(b);
    }
}
