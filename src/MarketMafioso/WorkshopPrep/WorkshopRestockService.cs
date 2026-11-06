using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Runtime;
using Franthropy.Dalamud.UI.Performance;
using MarketMafioso.Quartermaster;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopRestockService : IDisposable
{
    private readonly Configuration config;
    private readonly WorkshopProjectCatalog catalog;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly ExternalAutomationCoordinator automation;
    private readonly WorkshopRestockPersistence persistence;
    private DateTimeOffset nextPolicyAt;
    private bool disposed;
    private int refreshAccess = 1;
    public WorkshopVendorRestockRunner Runner { get; }
    public WorkshopQuartermasterRequestService Requests { get; }
    public DalamudGilVendorAccessReader Access { get; }

    public WorkshopRestockService(Configuration config, QuartermasterIpcClient quartermaster,
        WorkshopProjectCatalog catalog, IDataManager dataManager, IPlayerState playerState, IPluginLog log)
    {
        this.config = config;
        this.catalog = catalog;
        this.playerState = playerState;
        this.log = log;
        persistence = new(config.Save, ex => log.Error(ex, "[MarketMafioso] Restock checkpoint save failed; in-memory state retained."));
        Requests = new WorkshopQuartermasterRequestService(
            config,
            quartermaster,
            persistence.Request,
            persistIdentity: config.Save);
        automation = new ExternalAutomationCoordinator(
            new DalamudPluginDataStore(Plugin.PluginInterface),
            log);
        Access = new DalamudGilVendorAccessReader(
            Plugin.ClientState,
            playerState,
            Plugin.ObjectTable,
            Plugin.AetheryteList);
        var vendorRuntime = new DalamudGilVendorBuyRuntime(
            Access,
            new DalamudOrdinaryGilShop(Plugin.GameGui, dataManager),
            new DalamudVNavmeshTravel(Plugin.PluginInterface),
            new DalamudLifestreamAetheryteTravel(Plugin.PluginInterface),
            new DalamudLifestreamAethernetTravel(Plugin.PluginInterface),
            new DalamudLifestreamObjectInteractor(Plugin.PluginInterface),
            new DalamudTravelReadiness(
                Plugin.Condition,
                Plugin.GameGui,
                Plugin.ObjectTable,
                [
                    "RetainerList",
                    "SelectString",
                    "Talk",
                    "InventoryRetainer",
                    "InventoryRetainerLarge",
                    "InventoryRetainerSmall",
                ]),
            dataManager,
            Plugin.ClientState,
            Plugin.ObjectTable,
            Plugin.Condition,
            Plugin.CommandManager,
            beginAutomation: () =>
            {
                automation.SuppressTextAdvance();
                automation.SuppressTradeAutoConfirm();
            },
            endAutomation: () =>
            {
                automation.RestoreTextAdvance();
                automation.RestoreTradeAutoConfirm();
            });
        Runner = new WorkshopVendorRestockRunner(
            config,
            vendorRuntime,
            Requests,
            persistence.Request,
            message => log.Warning(message),
            persistenceError: () => persistence.Error);
        Requests.DiagnosticLog = message => log.Warning(message);
        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        if (!quartermaster.IsChangedSubscribed)
            log.Warning("[MarketMafioso] RQ Changed notifications unavailable; restock uses a 20-second fallback poll.");
    }

    [RenderFrameWorkJustification("Vendor buying drives game UI one coordinator step per framework update; other phases are event/deadline gated.", 1)]
    public void Tick()
    {
        if (disposed) return;
        try
        {
            if (Interlocked.Exchange(ref refreshAccess, 0) != 0)
                Access.RefreshAttunedAetherytes();
            // Remote work outlives the local policy: reconcile even when stopped or paused.
            var refreshed = TickRequestReconciliation(Requests, GetOwner);
            if (!Runner.IsRunning) return;
            var now = DateTimeOffset.UtcNow;
            var waiting = config.ActiveWorkshopVendorBuyRun is null &&
                config.ActiveWorkshopVendorRestockState is { Phase: WorkshopVendorRestockPhase.RetrieveFromQuartermaster, QuartermasterSubmitted: true };
            if (waiting && !refreshed) return;
            if (!waiting && config.ActiveWorkshopVendorBuyRun is null && now < nextPolicyAt) return;
            nextPolicyAt = now.AddSeconds(1);
            var owner = GetOwner();
            if (config.ActiveWorkshopVendorBuyRun is not null) Access.RefreshAttunedAetherytes();
            var requirements = catalog.BuildRequirements(config.WorkshopPrepQueue);
            var availability = WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
                requirements, new Dictionary<uint, int>(), []);
            Runner.Tick(WorkshopVendorProcurementPlanner.BuildQueueSignature(availability), owner);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Restock execution failed.");
            Runner.Pause("Restock execution failed: " + ex.Message);
        }
        finally
        {
            persistence.Flush();
        }
    }

    internal static bool TickRequestReconciliation(WorkshopQuartermasterRequestService requests,
        Func<QuartermasterOwnerScope> getOwner)
    {
        if (!requests.IsRefreshDue) return false;
        requests.PollOperationIfDue(getOwner());
        return true;
    }

    private QuartermasterOwnerScope GetOwner() => new(
        playerState.ContentId == 0 ? null : playerState.ContentId,
        playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
        playerState.CharacterName,
        playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Runner.Dispose();
        Requests.Dispose();
        automation.Dispose();
        persistence.Flush();
    }

    private void OnLogin() => Interlocked.Exchange(ref refreshAccess, 1);
    private void OnTerritoryChanged(uint _) => Interlocked.Exchange(ref refreshAccess, 1);
}
