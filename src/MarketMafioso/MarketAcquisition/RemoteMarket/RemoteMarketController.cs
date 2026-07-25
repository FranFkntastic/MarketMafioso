using System;
using System.IO;
using System.Text.Json;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed class RemoteMarketController : IDisposable
{
    private static readonly TimeSpan PurchaseDeadline = TimeSpan.FromSeconds(15);

    private readonly Configuration configuration;
    private readonly IMarketBoard marketBoard;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Func<uint, string?> resolveItemName;
    private readonly string evidenceDirectory;

    private RemoteMarketPurchaseAttempt? attempt;
    private string? lastOutcome;

    public RemoteMarketController(
        Configuration configuration,
        IMarketBoard marketBoard,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        IChatGui chatGui,
        IPluginLog log,
        Func<uint, string?> resolveItemName,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.marketBoard = marketBoard;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.log = log;
        this.resolveItemName = resolveItemName;
        evidenceDirectory = Path.Combine(pluginConfigDirectory, "remote-market");
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
    }

    public bool IsAvailable =>
        MarketAcquisitionUnlock.IsUnlocked(configuration) && configuration.EnableRemoteMarketPurchase;

    public unsafe bool IsMarketBoardResultVisible()
    {
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        return addon != null && addon->IsVisible;
    }

    public unsafe bool TryGetResultAnchor(out System.Numerics.Vector2 anchor)
    {
        anchor = default;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        if (addon == null || !addon->IsVisible)
            return false;
        anchor = new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true) + 8f, addon->Y + 48f);
        return true;
    }

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
    }

    public string OpenMarketBoard()
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (!clientState.IsLoggedIn)
            return "Log in first.";
        unsafe
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            if (agent == null)
                return "ItemSearch agent is unavailable.";
            agent->Show();
            var opened = agent->IsAgentActive();
            log.Information("[MarketMafioso] Remote market board opened. Territory={Territory} AgentActive={AgentActive}", clientState.TerritoryType, opened);
            return opened ? "Market board opened." : "Market board was shown but did not activate.";
        }
    }

    public RemoteMarketView GetView()
    {
        var listingCount = 0u;
        var selectedIndex = -1;
        RemoteMarketSelectionView? selection = null;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null)
            {
                listingCount = proxy->ListingCount;
                selectedIndex = GetSelectedListingIndex();
                if (selectedIndex >= 0 && (uint)selectedIndex < proxy->ListingCount)
                {
                    var listing = proxy->Listings[selectedIndex];
                    selection = new RemoteMarketSelectionView(
                        selectedIndex,
                        listing.ItemId,
                        resolveItemName(listing.ItemId) ?? $"Item {listing.ItemId}",
                        listing.IsHqItem,
                        listing.Quantity,
                        listing.UnitPrice,
                        listing.TotalTax,
                        (listing.UnitPrice * (ulong)listing.Quantity) + listing.TotalTax,
                        listing.ListingId,
                        listing.RetainerId);
                }
            }
        }

        return new RemoteMarketView(
            IsAvailable,
            (int)listingCount,
            selection,
            attempt is null
                ? null
                : new RemoteMarketAttemptView(
                    attempt.ItemName,
                    attempt.IsHighQuality,
                    attempt.Quantity,
                    attempt.TotalGil,
                    attempt.Phase,
                    attempt.FailureReason),
            lastOutcome);
    }

    public string? BeginPurchase()
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (attempt is not null)
            return "A purchase is already in progress.";
        var view = GetView();
        if (view.Selection is not { } selection)
            return "Select a listing in the market board window first.";
        attempt = new RemoteMarketPurchaseAttempt(
            selection,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            DateTimeOffset.UtcNow)
        {
            Phase = RemoteMarketPurchasePhase.AwaitingConfirmation,
        };
        return null;
    }

    public string? ConfirmPurchase()
    {
        if (attempt is not { Phase: RemoteMarketPurchasePhase.AwaitingConfirmation } pending)
            return "No staged purchase to confirm.";

        string? failure = null;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy == null)
            {
                failure = "ItemSearch proxy is unavailable.";
            }
            else if ((uint)pending.Selection.SelectedIndex >= proxy->ListingCount)
            {
                failure = "The staged listing is no longer present. Re-select it.";
            }
            else
            {
                var listing = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                    ref proxy->Listings[pending.Selection.SelectedIndex]);
                if (listing->ListingId != pending.Selection.ListingId)
                {
                    failure = "The selection changed since staging. Cancel and re-stage.";
                }
                else if (!proxy->SetLastPurchasedItem(listing))
                {
                    failure = "The client refused to stage the listing.";
                }
                else if (!proxy->SendPurchaseRequestPacket())
                {
                    failure = "The client refused to send the purchase request.";
                }
            }
        }

        if (failure is not null)
        {
            Fail(pending, failure);
            return failure;
        }

        pending.Phase = RemoteMarketPurchasePhase.Sent;
        pending.SentAtUtc = DateTimeOffset.UtcNow;
        pending.DeadlineAtUtc = pending.SentAtUtc + PurchaseDeadline;
        pending.GilBeforeSend = GetCurrentGil();
        log.Information(
            "[MarketMafioso] Remote market purchase sent. ListingId={ListingId} ItemId={ItemId} Quantity={Quantity} TotalGil={TotalGil}",
            pending.Selection.ListingId,
            pending.Selection.ItemId,
            pending.Selection.Quantity,
            pending.Selection.TotalGil);
        framework.RunOnTick(() =>
        {
            if (ReferenceEquals(attempt, pending) && pending.Phase == RemoteMarketPurchasePhase.Sent)
                ResolveIndeterminate(pending);
        }, PurchaseDeadline + TimeSpan.FromSeconds(1));
        DismissLingeringConfirmationDialogsSoon();
        return null;
    }

    public void CancelPurchase()
    {
        if (attempt is { Phase: RemoteMarketPurchasePhase.AwaitingConfirmation } pending)
        {
            pending.Phase = RemoteMarketPurchasePhase.Cancelled;
            Complete(pending, "Cancelled before sending.");
        }
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        if (attempt is not { Phase: RemoteMarketPurchasePhase.Sent } pending)
        {
            log.Information(
                "[MarketMafioso] Native-path purchase request observed outside a staged remote attempt. ListingId={ListingId} CatalogId={CatalogId} Quantity={Quantity} PricePerUnit={PricePerUnit}",
                purchase.ListingId,
                purchase.CatalogId,
                purchase.ItemQuantity,
                purchase.PricePerUnit);
            return;
        }
        pending.PacketObserved = true;
        pending.PacketMatchesIntent = RemoteMarketPurchaseMatcher.PacketMatchesIntent(
            pending.Selection.ListingId,
            pending.Selection.ItemId,
            pending.Selection.Quantity,
            pending.Selection.UnitPrice,
            purchase.ListingId,
            purchase.CatalogId,
            purchase.ItemQuantity,
            purchase.PricePerUnit);
        if (!pending.PacketMatchesIntent)
        {
            pending.Phase = RemoteMarketPurchasePhase.Conflicted;
            Complete(pending, "The observed purchase packet did not match the staged listing.");
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (attempt is not { Phase: RemoteMarketPurchasePhase.Sent } pending)
            return;
        if (!RemoteMarketPurchaseMatcher.ConfirmationMatchesIntent(
                pending.Selection.ItemId,
                pending.Selection.Quantity,
                purchase.CatalogId,
                purchase.ItemQuantity))
            return;
        pending.GilAfterResponse = GetCurrentGil();
        if (pending.GilBeforeSend is { } before && pending.GilAfterResponse is { } after)
        {
            var delta = (long)before - after;
            if (delta == (long)pending.TotalGil)
            {
                pending.Phase = RemoteMarketPurchasePhase.Confirmed;
                Complete(pending, $"Purchased {pending.Quantity}x {pending.ItemName} for {pending.TotalGil:N0} gil.");
                return;
            }
            if (delta == 0)
            {
                pending.Phase = RemoteMarketPurchasePhase.Failed;
                Complete(pending, "The server rejected the purchase. No gil moved.");
                return;
            }
            pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
            Complete(pending, $"A purchase response arrived but gil moved by {delta:N0} instead of {pending.TotalGil:N0}. Reconcile before retrying.");
            return;
        }
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        Complete(pending, "A purchase response arrived but gil state was unavailable. Reconcile before retrying.");
    }

    private void ResolveIndeterminate(RemoteMarketPurchaseAttempt pending)
    {
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        Complete(pending, "No purchase confirmation arrived before the deadline. Reconcile inventory and gil before retrying.");
    }

    private void Fail(RemoteMarketPurchaseAttempt pending, string reason)
    {
        pending.Phase = RemoteMarketPurchasePhase.Failed;
        Complete(pending, reason);
    }

    private void Complete(RemoteMarketPurchaseAttempt pending, string message)
    {
        pending.FailureReason = message;
        attempt = null;
        lastOutcome = $"{pending.Phase}: {message}";
        log.Information(
            "[MarketMafioso] Remote market purchase {Phase}. ListingId={ListingId} PacketObserved={PacketObserved} PacketMatchesIntent={PacketMatchesIntent} Message={Message}",
            pending.Phase,
            pending.Selection.ListingId,
            pending.PacketObserved,
            pending.PacketMatchesIntent,
            message);
        chatGui.Print($"[MMF] Remote market: {lastOutcome}");
        WriteEvidence(pending);
    }

    private void WriteEvidence(RemoteMarketPurchaseAttempt pending)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"remote-purchase-{pending.StagedAtUtc:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(pending.ToEvidence(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Remote market purchase evidence could not be written.");
        }
    }

    private void DismissLingeringConfirmationDialogsSoon() =>
        framework.RunOnTick(DismissLingeringConfirmationDialogs, TimeSpan.FromMilliseconds(500));

    private void DismissLingeringConfirmationDialogs()
    {
        string[] dialogAddons = ["SelectYesno", "SelectOk"];
        unsafe
        {
            foreach (var addonName in dialogAddons)
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName(addonName, 1).Address;
                if (addon == null || !addon->IsVisible)
                    continue;
                log.Information("[MarketMafioso] Remote market dismissing lingering dialog addon {AddonName}.", addonName);
                addon->Close(false);
            }
        }
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

    private static unsafe uint? GetCurrentGil()
    {
        var inventoryManager = InventoryManager.Instance();
        return inventoryManager == null ? null : inventoryManager->GetGil();
    }
}

internal enum RemoteMarketPurchasePhase
{
    AwaitingConfirmation,
    Sent,
    Confirmed,
    Failed,
    Cancelled,
    Conflicted,
    Indeterminate,
}

internal sealed record RemoteMarketSelectionView(
    int SelectedIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    ulong ListingId,
    ulong RetainerId);

internal sealed record RemoteMarketView(
    bool Available,
    int ListingCount,
    RemoteMarketSelectionView? Selection,
    RemoteMarketAttemptView? Attempt,
    string? LastOutcome);

internal sealed record RemoteMarketAttemptView(
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    ulong TotalGil,
    RemoteMarketPurchasePhase Phase,
    string? FailureReason);

internal sealed class RemoteMarketPurchaseAttempt(
    RemoteMarketSelectionView selection,
    uint territory,
    string position,
    DateTimeOffset stagedAtUtc)
{
    public RemoteMarketSelectionView Selection { get; } = selection;
    public uint Territory { get; } = territory;
    public string Position { get; } = position;
    public DateTimeOffset StagedAtUtc { get; } = stagedAtUtc;
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public RemoteMarketPurchasePhase Phase { get; set; } = RemoteMarketPurchasePhase.AwaitingConfirmation;
    public bool PacketObserved { get; set; }
    public bool PacketMatchesIntent { get; set; }
    public uint? GilBeforeSend { get; set; }
    public uint? GilAfterResponse { get; set; }
    public string? FailureReason { get; set; }
    public string ItemName => Selection.ItemName;
    public bool IsHighQuality => Selection.IsHighQuality;
    public uint Quantity => Selection.Quantity;
    public ulong TotalGil => Selection.TotalGil;

    public object ToEvidence() => new
    {
        StagedAtUtc,
        SentAtUtc,
        DeadlineAtUtc,
        Phase = Phase.ToString(),
        PacketObserved,
        PacketMatchesIntent,
        GilBeforeSend,
        GilAfterResponse,
        Territory,
        Position,
        Selection.SelectedIndex,
        Selection.ItemId,
        Selection.ItemName,
        Selection.IsHighQuality,
        Selection.Quantity,
        Selection.UnitPrice,
        Selection.TotalTax,
        Selection.TotalGil,
        Selection.ListingId,
        Selection.RetainerId,
    };
}
