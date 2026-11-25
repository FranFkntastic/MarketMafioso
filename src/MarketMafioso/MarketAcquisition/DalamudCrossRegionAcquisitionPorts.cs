using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.ProfileLogin;

namespace MarketMafioso.MarketAcquisition;

/// <summary>Live game state for the cross-regional pipeline. Reads run on the framework thread.</summary>
internal sealed class DalamudCrossRegionGameState : ICrossRegionGameState, IDisposable
{
    // Right after login the client reports a character before it can act; actions sent then are swallowed or refused.
    private static readonly TimeSpan LoginSettle = TimeSpan.FromSeconds(5);

    // Matches Quartermaster's CourierGame.Bags() (Inventory1..4 only; crystals are not courier-observed).
    private static readonly InventoryType[] CarriedContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly IPlayerState playerState;
    private readonly ICharacterLoginDriver? loginDriver;
    private readonly Func<bool> otherAutomationActive;
    private readonly ICondition? condition;
    private readonly IClientState? clientState;
    private readonly LifestreamIpc? lifestream;
    // Plugin load counts as a login: a character logged in before load gets the same settle time.
    private DateTimeOffset loginObservedAtUtc = DateTimeOffset.UtcNow;

    public DalamudCrossRegionGameState(
        IPlayerState playerState,
        ICharacterLoginDriver? loginDriver,
        Func<bool> otherAutomationActive,
        ICondition? condition = null,
        IClientState? clientState = null,
        LifestreamIpc? lifestream = null)
    {
        this.playerState = playerState;
        this.loginDriver = loginDriver;
        this.otherAutomationActive = otherAutomationActive;
        this.condition = condition;
        this.clientState = clientState;
        this.lifestream = lifestream;
        if (clientState is not null)
            clientState.Login += OnLogin;
    }

    public string? ActionBlocker
    {
        get
        {
            if (playerState.ContentId == 0)
                return "no character is logged in";
            if (condition is not null && PlayerActionReadiness.Blocker(condition) is { } blocker)
                return blocker;
            var sinceLogin = DateTimeOffset.UtcNow - loginObservedAtUtc;
            if (sinceLogin < LoginSettle)
                return $"the character logged in {sinceLogin.TotalSeconds:N0}s ago";
            if (lifestream is { IsAvailable: true } && lifestream.TryIsBusy(out var busy) && busy)
                return "Lifestream is busy";
            return null;
        }
    }

    public void Dispose()
    {
        if (clientState is not null)
            clientState.Login -= OnLogin;
    }

    private void OnLogin() => loginObservedAtUtc = DateTimeOffset.UtcNow;

    public CrossRegionCharacter? LoggedInCharacter =>
        playerState.ContentId == 0 || !playerState.HomeWorld.IsValid || string.IsNullOrWhiteSpace(playerState.CharacterName)
            ? null
            : new CrossRegionCharacter(
                playerState.ContentId,
                playerState.HomeWorld.Value.RowId,
                playerState.CharacterName,
                playerState.HomeWorld.Value.Name.ToString());

    public string? CurrentWorldName => playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : null;

    public unsafe ulong? CarriedGil
    {
        get
        {
            var manager = InventoryManager.Instance();
            if (manager == null || playerState.ContentId == 0)
                return null;
            // Gil reads 0 until the currency container loads after login; that is unknown, not broke.
            var currency = manager->GetInventoryContainer(InventoryType.Currency);
            return currency == null || !currency->IsLoaded ? null : manager->GetGil();
        }
    }

    public bool OtherAutomationActive => otherAutomationActive();

    public unsafe IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags()
    {
        var manager = InventoryManager.Instance();
        if (manager == null || playerState.ContentId == 0)
            return null;
        var stacks = new List<CrossRegionBagStack>();
        foreach (var type in CarriedContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return null;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    continue;
                stacks.Add(new CrossRegionBagStack(slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), (uint)slot->Quantity));
            }
        }
        return stacks;
    }

    public bool TrySwitchCharacter(CrossRegionCharacter target, out string message)
    {
        if (loginDriver is null)
        {
            LastSwitchRefusalIsFinal = true;
            message = "Character switching is unavailable (no Lifestream login driver).";
            return false;
        }
        var result = loginDriver.TryBegin(target.CharacterName, target.HomeWorldName, changeCharacter: true);
        message = string.IsNullOrWhiteSpace(result.Message) ? result.Code : result.Message;
        // NotReady (busy, mid-login) and IpcFailure (Lifestream reloading) can clear; Rejected and InvalidRequest cannot.
        LastSwitchRefusalIsFinal = !result.Success && result.Code is not ("NotReady" or "IpcFailure");
        return result.Success;
    }

    public bool LastSwitchRefusalIsFinal { get; private set; }
}

/// <summary>
/// Drives the ordinary Market Acquisition route engine for the shopper. The plan is re-prepared from
/// the session's persisted request document (the Workbench's prepared plan does not survive a
/// character switch), and only runs this adapter started forward confirmed purchases.
/// </summary>
internal sealed class MarketAcquisitionCrossRegionShoppingRoute : ICrossRegionShoppingRoute, IDisposable
{
    private readonly MarketAcquisitionRouteEngine engine;
    private readonly MarketAcquisitionRequestWorkspace workspace;
    private readonly IMarketAcquisitionInventoryObserver carriedObserver;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly Func<string?> currentWorld;
    private readonly Func<TimeSpan> recentWorldTtl;
    private readonly Func<MarketAcquisitionRouteDiagnosticsLevel> diagnosticsLevel;
    private readonly Func<bool> opportunisticChecks;
    private readonly Func<ulong?> carriedGil;
    private CancellationTokenSource? prepareCancellation;
    private Task<(MarketAcquisitionClaimView Request, MarketAcquisitionPlan Plan, string Message)>? prepareTask;
    private CrossRegionAcquisitionSession? preparingFor;
    private MarketAcquisitionClaimView? ownedClaim;
    private bool ownsRun;
    private CrossRegionShoppingState? settledState;
    private string statusMessage = string.Empty;
    private IReadOnlyDictionary<string, uint> preparedPrimaryBaselines = new Dictionary<string, uint>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, uint>? preparedShopperStartingStock;

    public MarketAcquisitionCrossRegionShoppingRoute(
        MarketAcquisitionRouteEngine engine,
        MarketAcquisitionRequestWorkspace workspace,
        IMarketAcquisitionInventoryObserver carriedObserver,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        Func<string?> currentWorld,
        Func<TimeSpan> recentWorldTtl,
        Func<MarketAcquisitionRouteDiagnosticsLevel> diagnosticsLevel,
        Func<bool> opportunisticChecks,
        Func<ulong?> carriedGil)
    {
        this.engine = engine;
        this.workspace = workspace;
        this.carriedObserver = carriedObserver;
        this.uiAutomation = uiAutomation;
        this.currentWorld = currentWorld;
        this.recentWorldTtl = recentWorldTtl;
        this.diagnosticsLevel = diagnosticsLevel;
        this.opportunisticChecks = opportunisticChecks;
        this.carriedGil = carriedGil;
        engine.ConfirmedPurchaseRecorded += OnEnginePurchase;
    }

    public event Action<MarketAcquisitionConfirmedPurchase>? ConfirmedPurchase;

    public CrossRegionShoppingState State
    {
        get
        {
            if (prepareTask is not null)
                return CrossRegionShoppingState.Preparing;
            if (settledState is { } settled)
                return settled;
            if (!ownsRun)
                return CrossRegionShoppingState.NotStarted;
            return engine.CreateSnapshot().RouteState switch
            {
                "Running" => CrossRegionShoppingState.Running,
                "Paused" => CrossRegionShoppingState.Paused,
                "Completed" => CrossRegionShoppingState.Completed,
                "Failed" => CrossRegionShoppingState.Failed,
                _ => CrossRegionShoppingState.Stopped,
            };
        }
    }

    public string StatusMessage => ownsRun && settledState is null && prepareTask is null
        ? engine.CreateSnapshot().StatusMessage
        : statusMessage;

    public bool CanResumeInPlace
    {
        get
        {
            if (!ownsRun || settledState is not null || prepareTask is not null || ownedClaim is null)
                return false;
            var snapshot = engine.CreateSnapshot();
            return snapshot.IsPaused || snapshot.CanRecover;
        }
    }

    public IReadOnlyDictionary<string, uint> CaptureShopperStartingStock(CrossRegionAcquisitionSession session)
    {
        var document = session.RequestDocumentSnapshot ?? throw new InvalidOperationException("The session has no request document.");
        var stock = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in TargetLines(workspace.ResolveLocalExecutionRequest(document)))
        {
            var observed = carriedObserver.Observe(line.ItemId, line.HqPolicy);
            if (!observed.IsAvailable)
                throw new InvalidOperationException($"Shopper bags are not readable yet ({observed.Message}).");
            stock[line.LineId] = observed.Quantity;
        }
        return stock;
    }

    public void BeginPrepareAndStart(CrossRegionAcquisitionSession session)
    {
        CancelPreparation();
        ownsRun = false;
        ownedClaim = null;
        settledState = null;
        var document = session.RequestDocumentSnapshot;
        if (document is null)
        {
            Settle(CrossRegionShoppingState.Failed, "The session has no request document to shop from.");
            return;
        }
        var world = currentWorld();
        if (string.IsNullOrWhiteSpace(world))
        {
            Settle(CrossRegionShoppingState.Failed, "Current world is unavailable.");
            return;
        }
        // A shopper left outside the target region (e.g. on the bridge world) plans from home instead.
        if (!CrossRegionAcquisitionCoordinator.RegionMatches(MarketAcquisitionWorkbenchCompositionInsights.RegionForWorld(world), session.TargetRegion) &&
            !string.IsNullOrWhiteSpace(session.Shopper?.HomeWorldName))
            world = session.Shopper.HomeWorldName;
        if (!TryReadMaxSafeSpend(out var maxSafeSpend, out var gilProblem))
        {
            Settle(CrossRegionShoppingState.Failed, gilProblem);
            return;
        }
        if (maxSafeSpend == 0)
        {
            Settle(CrossRegionShoppingState.Completed, NoSpendableGilMessage);
            return;
        }
        var (resumedDocument, keptOrdinals) = ResumedDocument(document, session.PurchasedManifest);
        if (resumedDocument.Lines.Count == 0)
        {
            Settle(CrossRegionShoppingState.Completed, "All shopping targets are already satisfied.");
            return;
        }
        IReadOnlyDictionary<string, uint> overrides;
        try
        {
            // Line ids are ordinal-based; carry the session's per-line baselines over to the resumed ids.
            var originalIds = MarketAcquisitionPlanPreparationService.GetPlanLines(workspace.ResolveLocalExecutionRequest(document))
                .ToDictionary(line => line.Ordinal, line => line.LineId);
            var resumedRequest = workspace.ResolveLocalExecutionRequest(resumedDocument);
            var lineIds = MarketAcquisitionPlanPreparationService.GetPlanLines(resumedRequest)
                .ToDictionary(line => originalIds[keptOrdinals[line.Ordinal]], line => line.LineId, StringComparer.Ordinal);
            preparedPrimaryBaselines = RemapLineIds(session.PrimaryStockBaselines, lineIds);
            preparedShopperStartingStock = session.ShopperStartingStock is null ? null : RemapLineIds(session.ShopperStartingStock, lineIds);
            overrides = BuildBaselineOverrides(preparedPrimaryBaselines, preparedShopperStartingStock, resumedRequest);
        }
        catch (Exception exception)
        {
            Settle(CrossRegionShoppingState.Failed, exception.Message);
            return;
        }
        document = resumedDocument;
        preparingFor = session;
        statusMessage = $"Preparing the {session.TargetRegion} shop route from {world}.";
        prepareCancellation = new CancellationTokenSource();
        prepareTask = workspace.PrepareDetachedPlanAsync(document, world, recentWorldTtl(), overrides, prepareCancellation.Token);
    }

    public void Tick()
    {
        if (prepareTask is not { IsCompleted: true } completed)
            return;
        var session = preparingFor;
        prepareTask = null;
        preparingFor = null;
        if (completed.IsCanceled || session is null)
        {
            Settle(CrossRegionShoppingState.Stopped, "Shop route preparation was cancelled.");
            return;
        }
        if (completed.IsFaulted)
        {
            Settle(CrossRegionShoppingState.Failed,
                $"Shop route preparation failed: {completed.Exception?.GetBaseException().Message}");
            return;
        }
        var (request, plan, message) = completed.Result;
        if (plan.WorldBatches.Count == 0)
        {
            Settle(CrossRegionShoppingState.Completed, string.IsNullOrWhiteSpace(message) ? "No purchasable listings remain." : message);
            return;
        }
        if (plan.RunSpendCap > 0)
        {
            // A re-prepared route keeps the original run budget minus what this session already spent.
            if (session.TotalSpentGil >= plan.RunSpendCap)
            {
                Settle(CrossRegionShoppingState.Completed, "The run budget is already spent.");
                return;
            }
            plan = plan with { RunSpendCap = (uint)(plan.RunSpendCap - session.TotalSpentGil) };
        }
        // Never spend into the teleport reserve the return haul needs on the shopper.
        if (!TryReadMaxSafeSpend(out var maxSafeSpend, out var gilProblem))
        {
            Settle(CrossRegionShoppingState.Failed, gilProblem);
            return;
        }
        if (maxSafeSpend == 0)
        {
            Settle(CrossRegionShoppingState.Completed, NoSpendableGilMessage);
            return;
        }
        if (plan.RunSpendCap == 0 || plan.RunSpendCap > maxSafeSpend)
            plan = plan with { RunSpendCap = (uint)Math.Min(maxSafeSpend, uint.MaxValue) };
        if (engine.IsRouteActive)
        {
            Settle(CrossRegionShoppingState.Failed, "Another Market Acquisition route is already active.");
            return;
        }
        var level = diagnosticsLevel();
        var baseline = new MarketAcquisitionCrossRegionBaseline(
            new Dictionary<string, uint>(preparedPrimaryBaselines, StringComparer.Ordinal),
            preparedShopperStartingStock is null
                ? new Dictionary<string, uint>(StringComparer.Ordinal)
                : new Dictionary<string, uint>(preparedShopperStartingStock, StringComparer.Ordinal));
        var result = engine.Start(
            plan,
            request,
            level != MarketAcquisitionRouteDiagnosticsLevel.Off,
            opportunisticChecks(),
            exactAcquisitionContract: null,
            workbenchDocument: session.RequestDocumentSnapshot,
            diagnosticsLevel: level,
            crossRegionBaseline: baseline);
        if (result.TargetsAlreadyMet)
        {
            Settle(CrossRegionShoppingState.Completed, result.Message);
            return;
        }
        if (!result.Success)
        {
            Settle(CrossRegionShoppingState.Failed, result.Message);
            return;
        }
        ownsRun = true;
        ownedClaim = request;
        workspace.ReplacePreparedPlan(plan);
        engine.ReportRouteProgress();
    }

    public bool Pause(out string message)
    {
        if (!ownsRun || settledState is not null)
        {
            message = "No cross-regional shop route is running.";
            return false;
        }
        var result = engine.Pause();
        engine.ReportRouteProgress();
        message = result.Message;
        return result.Success;
    }

    public bool ResumeInPlace(out string message)
    {
        if (!CanResumeInPlace)
        {
            message = "The shop route cannot resume in place.";
            return false;
        }
        var result = engine.CreateSnapshot().IsPaused ? engine.Resume() : engine.Recover(ownedClaim!);
        engine.ReportRouteProgress();
        message = result.Message;
        return result.Success;
    }

    public void Stop()
    {
        if (prepareTask is not null)
        {
            CancelPreparation();
            Settle(CrossRegionShoppingState.Stopped, "Shop route preparation was stopped.");
            return;
        }
        if (ownsRun && engine.IsRouteActive)
        {
            engine.Stop();
            engine.ReportRouteProgress();
        }
    }

    /// <summary>True once no market-board window is left open.</summary>
    public bool TryCloseMarketBoard() => !uiAutomation.TryCloseMarketBoardWindows();

    public void Dispose()
    {
        engine.ConfirmedPurchaseRecorded -= OnEnginePurchase;
        CancelPreparation();
    }

    private void OnEnginePurchase(MarketAcquisitionConfirmedPurchase purchase)
    {
        if (ownsRun)
            ConfirmedPurchase?.Invoke(purchase);
    }

    private static readonly string NoSpendableGilMessage =
        $"Shopper has no carried gil above the {CrossRegionAcquisitionCoordinator.TravelFeeReserveGil:N0} gil travel reserve.";

    private bool TryReadMaxSafeSpend(out ulong maxSafeSpend, out string problem)
    {
        maxSafeSpend = 0;
        problem = string.Empty;
        if (carriedGil() is not { } gil)
        {
            problem = "Shopper carried gil is unreadable.";
            return false;
        }
        const ulong reserve = CrossRegionAcquisitionCoordinator.TravelFeeReserveGil;
        maxSafeSpend = gil > reserve ? gil - reserve : 0;
        return true;
    }

    /// <summary>
    /// The remaining request after this session's confirmed purchases. AllBelowThreshold lines carry no
    /// on-hand target, so a re-prepared route would otherwise buy their full quantity/budget again.
    /// TargetQuantity lines already resume through the primary baseline plus the shopper's bag delta.
    /// </summary>
    internal static (MarketAcquisitionRequestDocument Document, IReadOnlyList<int> KeptOrdinals) ResumedDocument(
        MarketAcquisitionRequestDocument document,
        IReadOnlyList<CrossRegionManifestLine> purchased)
    {
        var lines = new List<MarketAcquisitionRequestLineDocument>(document.Lines.Count);
        var kept = new List<int>(document.Lines.Count);
        for (var ordinal = 0; ordinal < document.Lines.Count; ordinal++)
        {
            var line = document.Lines[ordinal];
            if (purchased.Count == 0 || string.Equals(line.QuantityMode, "TargetQuantity", StringComparison.Ordinal))
            {
                lines.Add(line);
                kept.Add(ordinal);
                continue;
            }
            var matches = purchased
                .Where(bought => bought.ItemId == line.ItemId && MarketAcquisitionPolicy.HqMatches(line.HqPolicy, bought.IsHq))
                .ToArray();
            var boughtQuantity = matches.Aggregate(0UL, (total, bought) => total + bought.Quantity);
            var spentGil = matches.Aggregate(0UL, (total, bought) => total + bought.SpentGil);
            var resumed = line;
            if (line.MaxQuantity > 0)
            {
                if (boughtQuantity >= line.MaxQuantity)
                    continue;
                resumed = resumed with { MaxQuantity = (uint)(line.MaxQuantity - boughtQuantity) };
            }
            if (line.GilCap > 0)
            {
                if (spentGil >= line.GilCap)
                    continue;
                resumed = resumed with { GilCap = (uint)(line.GilCap - spentGil) };
            }
            lines.Add(resumed);
            kept.Add(ordinal);
        }
        return (document with { Lines = lines }, kept);
    }

    private void Settle(CrossRegionShoppingState state, string message)
    {
        settledState = state;
        statusMessage = message;
    }

    private void CancelPreparation()
    {
        try { prepareCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        prepareCancellation?.Dispose();
        prepareCancellation = null;
        prepareTask = null;
        preparingFor = null;
    }

    /// <summary>Primary baseline plus the shopper's own bag growth since shopping started.</summary>
    private IReadOnlyDictionary<string, uint> BuildBaselineOverrides(
        IReadOnlyDictionary<string, uint> primaryBaselines,
        IReadOnlyDictionary<string, uint>? shopperStartingStock,
        MarketAcquisitionClaimView request)
    {
        var overrides = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in TargetLines(request))
        {
            var observed = carriedObserver.Observe(line.ItemId, line.HqPolicy);
            if (!observed.IsAvailable)
                throw new InvalidOperationException($"Shopper bags are not readable yet ({observed.Message}).");
            var start = shopperStartingStock?.GetValueOrDefault(line.LineId) ?? observed.Quantity;
            var delta = observed.Quantity > start ? observed.Quantity - start : 0u;
            overrides[line.LineId] = checked(primaryBaselines.GetValueOrDefault(line.LineId) + delta);
        }
        return overrides;
    }

    private static Dictionary<string, uint> RemapLineIds(IReadOnlyDictionary<string, uint> values, IReadOnlyDictionary<string, string> lineIds)
    {
        var remapped = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (originalId, resumedId) in lineIds)
        {
            if (values.TryGetValue(originalId, out var value))
                remapped[resumedId] = value;
        }
        return remapped;
    }

    internal static IEnumerable<MarketAcquisitionBatchLineView> TargetLines(MarketAcquisitionClaimView request) =>
        MarketAcquisitionPlanPreparationService.GetPlanLines(request)
            .Where(line => line.QuantityMode == "TargetQuantity" && MarketAcquisitionTargetBases.CountsOnHand(line.TargetBasis));
}
