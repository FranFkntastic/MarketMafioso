using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.ProfileLogin;

namespace MarketMafioso.MarketAcquisition;

/// <summary>Live game state for the cross-regional pipeline. Reads run on the framework thread.</summary>
internal sealed class DalamudCrossRegionGameState : ICrossRegionGameState
{
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

    public DalamudCrossRegionGameState(IPlayerState playerState, ICharacterLoginDriver? loginDriver, Func<bool> otherAutomationActive)
    {
        this.playerState = playerState;
        this.loginDriver = loginDriver;
        this.otherAutomationActive = otherAutomationActive;
    }

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
            return manager == null ? null : manager->GetGil();
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
            message = "Character switching is unavailable (no Lifestream login driver).";
            return false;
        }
        var result = loginDriver.TryBegin(target.CharacterName, target.HomeWorldName, changeCharacter: true);
        message = string.IsNullOrWhiteSpace(result.Message) ? result.Code : result.Message;
        return result.Success;
    }
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
    private CancellationTokenSource? prepareCancellation;
    private Task<(MarketAcquisitionClaimView Request, MarketAcquisitionPlan Plan, string Message)>? prepareTask;
    private CrossRegionAcquisitionSession? preparingFor;
    private MarketAcquisitionClaimView? ownedClaim;
    private bool ownsRun;
    private CrossRegionShoppingState? settledState;
    private string statusMessage = string.Empty;

    public MarketAcquisitionCrossRegionShoppingRoute(
        MarketAcquisitionRouteEngine engine,
        MarketAcquisitionRequestWorkspace workspace,
        IMarketAcquisitionInventoryObserver carriedObserver,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        Func<string?> currentWorld,
        Func<TimeSpan> recentWorldTtl,
        Func<MarketAcquisitionRouteDiagnosticsLevel> diagnosticsLevel,
        Func<bool> opportunisticChecks)
    {
        this.engine = engine;
        this.workspace = workspace;
        this.carriedObserver = carriedObserver;
        this.uiAutomation = uiAutomation;
        this.currentWorld = currentWorld;
        this.recentWorldTtl = recentWorldTtl;
        this.diagnosticsLevel = diagnosticsLevel;
        this.opportunisticChecks = opportunisticChecks;
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
        IReadOnlyDictionary<string, uint> overrides;
        try
        {
            overrides = BuildBaselineOverrides(session, workspace.ResolveLocalExecutionRequest(document));
        }
        catch (Exception exception)
        {
            Settle(CrossRegionShoppingState.Failed, exception.Message);
            return;
        }
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
        if (engine.IsRouteActive)
        {
            Settle(CrossRegionShoppingState.Failed, "Another Market Acquisition route is already active.");
            return;
        }
        var level = diagnosticsLevel();
        var baseline = new MarketAcquisitionCrossRegionBaseline(
            new Dictionary<string, uint>(session.PrimaryStockBaselines, StringComparer.Ordinal),
            new Dictionary<string, uint>(session.ShopperStartingStock ?? [], StringComparer.Ordinal));
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
    private IReadOnlyDictionary<string, uint> BuildBaselineOverrides(CrossRegionAcquisitionSession session, MarketAcquisitionClaimView request)
    {
        var overrides = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in TargetLines(request))
        {
            var observed = carriedObserver.Observe(line.ItemId, line.HqPolicy);
            if (!observed.IsAvailable)
                throw new InvalidOperationException($"Shopper bags are not readable yet ({observed.Message}).");
            var start = session.ShopperStartingStock?.GetValueOrDefault(line.LineId) ?? observed.Quantity;
            var delta = observed.Quantity > start ? observed.Quantity - start : 0u;
            overrides[line.LineId] = checked(session.PrimaryStockBaselines.GetValueOrDefault(line.LineId) + delta);
        }
        return overrides;
    }

    internal static IEnumerable<MarketAcquisitionBatchLineView> TargetLines(MarketAcquisitionClaimView request) =>
        MarketAcquisitionPlanPreparationService.GetPlanLines(request)
            .Where(line => line.QuantityMode == "TargetQuantity" && MarketAcquisitionTargetBases.CountsOnHand(line.TargetBasis));
}
