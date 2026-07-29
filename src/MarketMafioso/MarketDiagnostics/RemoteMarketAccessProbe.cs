using System;
using System.IO;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class RemoteMarketAccessProbe : IDisposable
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(120);
    private const string ApprovedGameVersion = "2026.07.16.0001.0000";
    private const string PatchContractId = "mmf.remote-market-direct-purchase-probe";

    private readonly Configuration configuration;
    private readonly IMarketBoard marketBoard;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly string evidenceDirectory;

    private ProbeSession? session;
    private bool closeListenerRegistered;

    public RemoteMarketAccessProbe(
        Configuration configuration,
        IMarketBoard marketBoard,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IChatGui chatGui,
        IPluginLog log,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.marketBoard = marketBoard;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.log = log;
        evidenceDirectory = Path.Combine(pluginConfigDirectory, "market-diagnostics");
    }

    public bool IsEnabled => configuration.EnableMarketDiagnostics;

    public string BeginProbe()
    {
        if (!IsEnabled)
            return "Remote market probe is disabled. Enable market diagnostics in settings first.";
        if (session is not null)
            return "Remote market probe is already armed. Search an item or wait for the window to expire.";
        if (!clientState.IsLoggedIn)
            return "Remote market probe requires a logged-in character.";

        var territory = clientState.TerritoryType;
        var position = objectTable.LocalPlayer?.Position.ToString() ?? "unavailable";

        var agentOpened = false;
        string agentStatus;
        unsafe
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            if (agent == null)
            {
                agentStatus = "ItemSearch agent unavailable";
            }
            else
            {
                agent->Show();
                agentOpened = agent->IsAgentActive();
                agentStatus = agentOpened ? "ItemSearch agent shown remotely" : "ItemSearch agent did not activate";
            }
        }

        session = new ProbeSession(DateTimeOffset.UtcNow, territory, position, agentOpened);
        marketBoard.OfferingsReceived += OnOfferingsReceived;
        marketBoard.HistoryReceived += OnHistoryReceived;
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearch", OnItemSearchFinalized);
        closeListenerRegistered = true;
        var armed = session;
        framework.RunOnTick(() =>
        {
            if (ReferenceEquals(session, armed))
                ExpireProbe();
        }, ProbeWindow);

        log.Information(
            "[MarketMafioso] Remote market probe armed. Territory={Territory} Position={Position} AgentOpened={AgentOpened}",
            territory,
            position,
            agentOpened);
        chatGui.Print($"[MMF] Remote market probe armed ({agentStatus}). Search any item in the Market Board window within {ProbeWindow.TotalSeconds:0}s.");
        return "Remote market probe armed. Search any item in the Market Board window.";
    }

    public void Dispose()
    {
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        marketBoard.HistoryReceived -= OnHistoryReceived;
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        if (closeListenerRegistered)
            addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnItemSearchFinalized);
    }

    public RemoteMarketProbeView GetView()
    {
        var listingCount = 0u;
        var waitingForListings = false;
        var selectedIndex = -1;
        uint? selectedUnitPrice = null;
        uint? selectedQuantity = null;
        uint? selectedTax = null;
        bool? selectedIsHq = null;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null)
            {
                listingCount = proxy->ListingCount;
                waitingForListings = proxy->WaitingForListings;
                selectedIndex = GetSelectedListingIndex();
                if (selectedIndex >= 0 && (uint)selectedIndex < proxy->ListingCount)
                {
                    var listing = proxy->Listings[selectedIndex];
                    selectedUnitPrice = listing.UnitPrice;
                    selectedQuantity = listing.Quantity;
                    selectedTax = listing.TotalTax;
                    selectedIsHq = listing.IsHqItem;
                }
            }
        }

        var purchaseBlockedReason = GetPurchaseBlockedReason(listingCount, selectedIndex);
        return new RemoteMarketProbeView(
            session is not null,
            session?.Verdict,
            session?.VerdictReason,
            (int)listingCount,
            waitingForListings,
            selectedIndex,
            selectedUnitPrice,
            selectedQuantity,
            selectedTax,
            selectedIsHq,
            session?.PurchaseRequestSent ?? false,
            session?.PurchaseResponseReceived ?? false,
            purchaseBlockedReason);
    }

    public string? TryPurchaseSelected()
    {
        if (session is null)
            return "Arm the probe first.";
        var view = GetView();
        if (view.PurchaseBlockedReason is not null)
            return view.PurchaseBlockedReason;

        string? failure = null;
        var sent = false;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return "ItemSearch proxy is unavailable.";
            var listing = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref proxy->Listings[view.SelectedIndex]);
            if (!proxy->SetLastPurchasedItem(listing))
            {
                failure = "The client refused to stage the selected listing.";
            }
            else
            {
                sent = proxy->SendPurchaseRequestPacket();
                if (!sent)
                    failure = "The client refused to send the purchase request.";
            }
        }

        if (failure is not null)
            return failure;
        if (!sent)
            return "The purchase request was not sent.";
        log.Information(
            "[MarketMafioso] Remote market probe dispatched proxy purchase of staged listing index {Index} (full stack).",
            view.SelectedIndex);
        chatGui.Print($"[MMF] Remote market probe: proxy purchase dispatched for listing #{view.SelectedIndex + 1} (full stack). Watching for the server response.");
        framework.RunOnTick(DismissLingeringConfirmationDialogs, TimeSpan.FromMilliseconds(500));
        return null;
    }

    private void DismissLingeringConfirmationDialogs()
    {
        string[] dialogAddons = ["SelectYesno", "SelectOk", "ItemSearchCompare"];
        unsafe
        {
            foreach (var addonName in dialogAddons)
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName(addonName, 1).Address;
                if (addon == null || !addon->IsVisible)
                    continue;
                log.Information("[MarketMafioso] Remote market probe dismissing lingering dialog addon {AddonName}.", addonName);
                addon->Close(false);
            }
        }
    }

    private string? GetPurchaseBlockedReason(uint listingCount, int selectedIndex)
    {
        if (session is null)
            return "Arm the probe first.";
        var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
        if (!compatibility.IsApproved)
            return compatibility.Message;
        if (session.PurchaseRequestSent && !session.PurchaseResponseReceived)
            return "A purchase response is still pending.";
        if (listingCount == 0)
            return "Search an item first.";
        if (selectedIndex < 0 || (uint)selectedIndex >= listingCount)
            return "Select a listing in the market board window.";
        return null;
    }

    private unsafe InfoProxyItemSearch* GetItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null ? null : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
    }

    private unsafe int GetSelectedListingIndex()
    {
        var addon = (AddonItemSearchResult*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        return addon == null || addon->Results == null ? -1 : addon->Results->SelectedItemIndex;
    }

    private void OnItemSearchFinalized(AddonEvent type, AddonArgs args)
    {
        if (session is null)
            return;
        ConcludeProbe(session.Verdict ?? "Closed", session.VerdictReason ?? "market board window closed before any server response");
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (session is null)
            return;
        session.OfferingsPages += 1;
        session.OfferingsListings += offerings.ItemListings.Count;
        session.FirstOfferingsItemId ??= offerings.ItemListings.Count > 0 ? offerings.ItemListings[0].ItemId : null;
        MarkConfirmed($"server returned {offerings.ItemListings.Count} listings with no market board interaction");
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
    {
        if (session is null)
            return;
        session.HistoryReceived = true;
        session.HistoryItemId = history.ItemId;
        session.HistoryEntries = history.HistoryListings.Count;
        MarkConfirmed($"server returned {history.HistoryListings.Count} sale history entries with no market board interaction");
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        if (session is null)
            return;
        session.PurchaseRequestSent = true;
        session.PurchaseRequestCatalogId = purchase.CatalogId;
        session.PurchaseRequestQuantity = purchase.ItemQuantity;
        session.PurchaseRequestPricePerUnit = purchase.PricePerUnit;
        log.Information(
            "[MarketMafioso] Remote market probe observed purchase request. ItemId={ItemId} Quantity={Quantity} PricePerUnit={PricePerUnit}",
            purchase.CatalogId,
            purchase.ItemQuantity,
            purchase.PricePerUnit);
        chatGui.Print($"[MMF] Remote market probe: purchase request sent (item {purchase.CatalogId}, x{purchase.ItemQuantity} @ {purchase.PricePerUnit}g). Watching for the server response.");
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (session is null)
            return;
        session.PurchaseResponseReceived = true;
        session.PurchaseResponseCatalogId = purchase.CatalogId;
        session.PurchaseResponseQuantity = purchase.ItemQuantity;
        MarkConfirmed($"server completed a purchase (item {purchase.CatalogId}, x{purchase.ItemQuantity}) with no market board interaction");
    }

    private void MarkConfirmed(string reason)
    {
        if (session is null || session.Verdict is not null)
            return;
        session.Verdict = "Confirmed";
        session.VerdictReason = reason;
        log.Information("[MarketMafioso] Remote market probe Confirmed: {Reason}", reason);
        chatGui.Print($"[MMF] Remote market probe Confirmed: {reason}. Probe stays armed until the window closes.");
    }

    private void ExpireProbe()
    {
        if (session is null)
            return;
        ConcludeProbe(session.Verdict ?? "Inconclusive", session.VerdictReason ?? "no market board response observed within the probe window");
    }

    private void ConcludeProbe(string verdict, string reason)
    {
        var concluded = session;
        if (concluded is null)
            return;
        session = null;
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        marketBoard.HistoryReceived -= OnHistoryReceived;
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        if (closeListenerRegistered)
        {
            addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnItemSearchFinalized);
            closeListenerRegistered = false;
        }

        var evidence = new RemoteMarketAccessProbeEvidence(
            concluded.ArmedAtUtc,
            DateTimeOffset.UtcNow,
            concluded.Territory,
            concluded.Position,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            concluded.AgentOpened,
            verdict,
            reason,
            concluded.OfferingsPages,
            concluded.OfferingsListings,
            concluded.FirstOfferingsItemId,
            concluded.HistoryReceived,
            concluded.HistoryItemId,
            concluded.HistoryEntries,
            concluded.PurchaseRequestSent,
            concluded.PurchaseRequestCatalogId,
            concluded.PurchaseRequestQuantity,
            concluded.PurchaseRequestPricePerUnit,
            concluded.PurchaseResponseReceived,
            concluded.PurchaseResponseCatalogId,
            concluded.PurchaseResponseQuantity);
        WriteEvidence(evidence);

        log.Information("[MarketMafioso] Remote market probe {Verdict}: {Reason}", verdict, reason);
        chatGui.Print($"[MMF] Remote market probe {verdict}: {reason}.");
    }

    private void WriteEvidence(RemoteMarketAccessProbeEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"remote-access-probe-{evidence.ArmedAtUtc:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Remote market probe evidence could not be written.");
        }
    }

    private sealed class ProbeSession(DateTimeOffset armedAtUtc, uint territory, string position, bool agentOpened)
    {
        public DateTimeOffset ArmedAtUtc { get; } = armedAtUtc;
        public uint Territory { get; } = territory;
        public string Position { get; } = position;
        public bool AgentOpened { get; } = agentOpened;
        public int OfferingsPages { get; set; }
        public int OfferingsListings { get; set; }
        public uint? FirstOfferingsItemId { get; set; }
        public bool HistoryReceived { get; set; }
        public uint? HistoryItemId { get; set; }
        public int HistoryEntries { get; set; }
        public bool PurchaseRequestSent { get; set; }
        public uint? PurchaseRequestCatalogId { get; set; }
        public uint? PurchaseRequestQuantity { get; set; }
        public uint? PurchaseRequestPricePerUnit { get; set; }
        public bool PurchaseResponseReceived { get; set; }
        public uint? PurchaseResponseCatalogId { get; set; }
        public uint? PurchaseResponseQuantity { get; set; }
        public string? Verdict { get; set; }
        public string? VerdictReason { get; set; }
    }

    private sealed record RemoteMarketAccessProbeEvidence(
        DateTimeOffset ArmedAtUtc,
        DateTimeOffset ConcludedAtUtc,
        uint Territory,
        string PositionWhenArmed,
        string PositionWhenConcluded,
        bool AgentOpened,
        string Verdict,
        string Reason,
        int OfferingsPages,
        int OfferingsListings,
        uint? FirstOfferingsItemId,
        bool HistoryObserved,
        uint? HistoryItemId,
        int HistoryEntries,
        bool PurchaseRequestSent,
        uint? PurchaseRequestCatalogId,
        uint? PurchaseRequestQuantity,
        uint? PurchaseRequestPricePerUnit,
        bool PurchaseResponseReceived,
        uint? PurchaseResponseCatalogId,
        uint? PurchaseResponseQuantity);
}

internal sealed record RemoteMarketProbeView(
    bool Armed,
    string? Verdict,
    string? VerdictReason,
    int ListingCount,
    bool WaitingForListings,
    int SelectedIndex,
    uint? SelectedUnitPrice,
    uint? SelectedQuantity,
    uint? SelectedTax,
    bool? SelectedIsHq,
    bool PurchaseRequestSent,
    bool PurchaseResponseReceived,
    string? PurchaseBlockedReason);
