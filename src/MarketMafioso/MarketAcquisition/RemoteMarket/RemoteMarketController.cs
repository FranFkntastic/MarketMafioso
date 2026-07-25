using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Network.Structures;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed class RemoteMarketController : IDisposable
{
    private static readonly TimeSpan PurchaseDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BatchPacingDelay = TimeSpan.FromMilliseconds(1600);

    private static readonly ConditionFlag[] PurchaseBlockingConditions =
    [
        ConditionFlag.Emoting,
        ConditionFlag.Mounted,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.PlayingMiniGame,
        ConditionFlag.Occupied,
        ConditionFlag.InCombat,
        ConditionFlag.Occupied30,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
    ];

    private readonly Configuration configuration;
    private readonly IMarketBoard marketBoard;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IToastGui toastGui;
    private readonly ICondition condition;
    private readonly Hook<ZoneClient.Delegates.SendPacket>? sendPacketHook;
    private readonly List<string> pendingToastCapture = [];
    private readonly List<string> pendingOutgoingCapture = [];
    private DateTimeOffset toastCaptureUntilUtc;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Func<uint, string?> resolveItemName;
    private readonly string evidenceDirectory;

    private readonly List<RemoteMarketBatchItem> batchItems = [];
    private RemoteMarketPurchaseAttempt? attempt;
    private string? lastOutcome;
    private readonly HashSet<ulong> purchasedListingIds = [];

    public RemoteMarketController(
        Configuration configuration,
        IMarketBoard marketBoard,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IFramework framework,
        IGameGui gameGui,
        IToastGui toastGui,
        IGameInteropProvider interopProvider,
        ICondition condition,
        IChatGui chatGui,
        IPluginLog log,
        Func<uint, string?> resolveItemName,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.marketBoard = marketBoard;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.framework = framework;
        this.gameGui = gameGui;
        this.toastGui = toastGui;
        this.condition = condition;
        try
        {
            unsafe
            {
                sendPacketHook = interopProvider.HookFromAddress<ZoneClient.Delegates.SendPacket>(
                    (nint)ZoneClient.MemberFunctionPointers.SendPacket,
                    SendPacketDetour);
                sendPacketHook.Enable();
            }
        }
        catch (Exception exception)
        {
            sendPacketHook = null;
            log.Warning(exception, "[MarketMafioso] SendPacket observation hook unavailable for remote market probing.");
        }
        this.chatGui = chatGui;
        this.log = log;
        this.resolveItemName = resolveItemName;
        evidenceDirectory = Path.Combine(pluginConfigDirectory, "remote-market");
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        toastGui.Toast += OnToastObserved;
        toastGui.ErrorToast += OnErrorToastObserved;
    }

    public bool IsAvailable =>
        MarketAcquisitionUnlock.IsUnlocked(configuration) && configuration.EnableRemoteMarketPurchase;

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        toastGui.Toast -= OnToastObserved;
        toastGui.ErrorToast -= OnErrorToastObserved;
        sendPacketHook?.Dispose();
    }

    private unsafe bool SendPacketDetour(ZoneClient* zoneClient, nint packet, uint argument3, uint argument4, bool argument5)
    {
        if (packet != 0 && DateTimeOffset.UtcNow <= toastCaptureUntilUtc)
        {
            var opcode = *(uint*)packet;
            var target = *(uint*)(packet + 0x10);
            pendingOutgoingCapture.Add($"0x{opcode:X} (target {target:X})");
        }
        return sendPacketHook!.Original(zoneClient, packet, argument3, argument4, argument5);
    }

    private void OnToastObserved(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref Dalamud.Game.Gui.Toast.ToastOptions options, ref bool isHandled) =>
        CaptureToast("normal", message.TextValue);

    private void OnErrorToastObserved(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool isHandled) =>
        CaptureToast("error", message.TextValue);

    private void CaptureToast(string kind, string text)
    {
        if (DateTimeOffset.UtcNow > toastCaptureUntilUtc || string.IsNullOrWhiteSpace(text))
            return;
        pendingToastCapture.Add($"{kind}: {text}");
    }

    public string? GetPurchaseContextBlockReason()
    {
        foreach (var flag in PurchaseBlockingConditions)
        {
            if (condition[flag])
                return $"Cannot purchase while {flag}.";
        }
        if (configuration.RemoteMarketRejectedTerritories.Contains(clientState.TerritoryType))
            return "Purchases have been rejected in this area before.";
        return null;
    }

    public void ClearRejectedTerritories()
    {
        configuration.RemoteMarketRejectedTerritories.Clear();
        configuration.Save();
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

    public unsafe string OpenMarketBoardViaObject()
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (!clientState.IsLoggedIn)
            return "Log in first.";

        var player = objectTable.LocalPlayer;
        if (player is null)
            return "The local player is unavailable.";

        GameObject* board = null;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? boardWrapper = null;
        var distance = 0f;
        foreach (var candidate in objectTable)
        {
            if (candidate.ObjectKind != DalamudObjectKind.EventObj ||
                !candidate.Name.TextValue.Contains("Market Board", StringComparison.OrdinalIgnoreCase))
                continue;
            board = (GameObject*)candidate.Address;
            boardWrapper = candidate;
            distance = System.Numerics.Vector3.Distance(player.Position, candidate.Position);
            break;
        }

        if (board == null)
            return "No market board object is loaded in this zone.";

        var startedAtUtc = DateTimeOffset.UtcNow;
        var territory = clientState.TerritoryType;
        var position = player.Position;
        unsafe
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return "The target system is unavailable.";

            var originalRadius = board->HitboxRadius;
            var originalPosition = board->Position;
            var originalDefaultPosition = board->DefaultPosition;
            var nativePlayer = (GameObject*)player.Address;
            var originalPlayerPosition = nativePlayer->Position;
            board->HitboxRadius = 9999f;
            board->Position = originalPosition;
            board->DefaultPosition = originalDefaultPosition;
            nativePlayer->Position = originalPosition;
            targetManager.Target = boardWrapper;
            targetSystem->InteractWithObject(board, false);

            var boardId = board->EntityId;
            var radius = originalRadius;
            var boardPosition = originalPosition;
            pendingToastCapture.Clear();
            toastCaptureUntilUtc = startedAtUtc + TimeSpan.FromSeconds(2);
            framework.RunOnTick(
                () =>
                {
                    WriteBoardObjectProbeEvidence(startedAtUtc, territory, position, boardId, boardPosition, radius);
                    nativePlayer->Position = originalPlayerPosition;
                    board->HitboxRadius = radius;
                },
                TimeSpan.FromMilliseconds(750));

            log.Information(
                "[MarketMafioso] Radius-and-elevation-augmented market board interaction from {Distance:F1} yalms (radius {Original:F2} -> 9999, Y {OriginalY:F2} -> {PlayerY:F2}, restored).",
                distance,
                originalRadius,
                originalPosition.Y,
                player.Position.Y);
            return $"Interacted with the market board object from {distance:F1} yalms through the stock path (radius and elevation restored).";
        }
    }

    private void WriteBoardObjectProbeEvidence(
        DateTimeOffset startedAtUtc,
        uint territory,
        System.Numerics.Vector3 playerPosition,
        uint boardId,
        System.Numerics.Vector3 boardPosition,
        float originalRadius)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(evidenceDirectory, $"board-object-probe-{startedAtUtc:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                startedAtUtc,
                concludedAtUtc = DateTimeOffset.UtcNow,
                territory,
                playerPosition = playerPosition.ToString(),
                boardId,
                boardPosition = boardPosition.ToString(),
                originalRadius,
                boardWindowOpen = IsMarketBoardResultVisible(),
                toasts = pendingToastCapture.ToArray(),
                outgoingPackets = pendingOutgoingCapture.ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            pendingToastCapture.Clear();
            pendingOutgoingCapture.Clear();
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Board object probe evidence could not be written.");
        }
    }

    public RemoteMarketView GetView()
    {
        var listings = new List<RemoteMarketListingView>();
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null)
            {
                var count = (int)Math.Min(proxy->ListingCount, 50);
                for (var index = 0; index < count; index++)
                {
                    var listing = proxy->Listings[index];
                    listings.Add(new RemoteMarketListingView(
                        listing.ListingId,
                        listing.ItemId,
                        resolveItemName(listing.ItemId) ?? $"Item {listing.ItemId}",
                        listing.IsHqItem,
                        listing.Quantity,
                        listing.UnitPrice,
                        listing.TotalTax,
                        (listing.UnitPrice * (ulong)listing.Quantity) + listing.TotalTax,
                        listing.MateriaCount,
                        purchasedListingIds.Contains(listing.ListingId),
                        batchItems.FirstOrDefault(item => item.ListingId == listing.ListingId)?.Status));
                }
            }
        }

        var batch = batchItems.Count == 0
            ? null
            : new RemoteMarketBatchView(
                batchItems.Count,
                batchItems.Count(item => item.Status is RemoteMarketBatchItemStatus.Confirmed or RemoteMarketBatchItemStatus.Failed or RemoteMarketBatchItemStatus.Skipped),
                batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Failed),
                attempt is not null);

        return new RemoteMarketView(
            IsAvailable,
            listings,
            batch,
            lastOutcome,
            GetPurchaseContextBlockReason(),
            GetCurrentGil());
    }

    public string? BeginBatch(IReadOnlyCollection<ulong> selectedListingIds)
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (batchItems.Count > 0)
            return "A purchase batch is already in progress.";
        if (GetPurchaseContextBlockReason() is { } contextBlock)
            return contextBlock;

        var staged = new List<RemoteMarketSelectionView>();
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return "ItemSearch proxy is unavailable.";
            foreach (var listingId in selectedListingIds)
            {
                var listing = FindListing(proxy, listingId);
                if (listing is null)
                    return "A selected listing is no longer in the results. Re-search to refresh.";
                if (purchasedListingIds.Contains(listingId))
                    return "One of the selected listings was already purchased. Re-search to refresh.";
                staged.Add(ToSelection(listing.Value, resolveItemName(listing.Value.ItemId) ?? $"Item {listing.Value.ItemId}"));
            }
        }

        if (staged.Count == 0)
            return "Select at least one listing.";

        foreach (var selection in staged)
            batchItems.Add(new RemoteMarketBatchItem(selection.ListingId, selection, RemoteMarketBatchItemStatus.Queued));
        AdvanceBatch();
        return null;
    }

    public void CancelBatch()
    {
        foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
            item.Status = RemoteMarketBatchItemStatus.Skipped;
        if (attempt is null)
            FinishBatch("Batch cancelled.");
    }

    private void AdvanceBatch()
    {
        if (attempt is not null)
            return;
        var next = batchItems.FirstOrDefault(item => item.Status == RemoteMarketBatchItemStatus.Queued);
        if (next is null)
        {
            FinishBatch(null);
            return;
        }
        if (GetPurchaseContextBlockReason() is { } contextBlock)
        {
            foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
                item.Status = RemoteMarketBatchItemStatus.Skipped;
            FinishBatch(contextBlock);
            return;
        }

        next.Status = RemoteMarketBatchItemStatus.Sending;
        var pending = new RemoteMarketPurchaseAttempt(
            next.Selection,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            DateTimeOffset.UtcNow);
        attempt = pending;

        string? failure = null;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            var listing = proxy == null ? null : FindListing(proxy, next.ListingId);
            if (listing is null)
            {
                failure = "The listing left the results before it could be staged.";
            }
            else
            {
                var staged = listing.Value;
                var listingPointer = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref staged);
                if (!proxy->SetLastPurchasedItem(listingPointer))
                    failure = "The client refused to stage the listing.";
                else if (!proxy->SendPurchaseRequestPacket())
                    failure = "The client refused to send the purchase request.";
            }
        }

        if (failure is not null)
        {
            next.Status = RemoteMarketBatchItemStatus.Failed;
            Fail(pending, failure);
            return;
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
    }

    private void FinishBatch(string? abortReason)
    {
        if (batchItems.Count == 0)
            return;
        var confirmed = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Confirmed);
        var failed = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Failed);
        var skipped = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Skipped);
        lastOutcome = abortReason is not null
            ? $"Batch aborted: {abortReason} ({confirmed} confirmed, {failed} failed, {skipped} skipped)"
            : $"Batch complete: {confirmed} confirmed, {failed} failed, {skipped} skipped.";
        log.Information("[MarketMafioso] Remote market {Outcome}", lastOutcome);
        chatGui.Print($"[MMF] Remote market: {lastOutcome}");
        batchItems.Clear();
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
            MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
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
                purchasedListingIds.Add(pending.Selection.ListingId);
                pending.Phase = RemoteMarketPurchasePhase.Confirmed;
                MarkActiveItem(RemoteMarketBatchItemStatus.Confirmed);
                Complete(pending, $"Purchased {pending.Quantity}x {pending.ItemName} for {pending.TotalGil:N0} gil.");
                return;
            }
            if (delta == 0)
            {
                NoteRejectedTerritory();
                pending.Phase = RemoteMarketPurchasePhase.Failed;
                MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
                Complete(pending, "The server rejected the purchase. No gil moved.");
                return;
            }
            pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
            MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
            Complete(pending, $"A purchase response arrived but gil moved by {delta:N0} instead of {pending.TotalGil:N0}. Reconcile before retrying.");
            return;
        }
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
        Complete(pending, "A purchase response arrived but gil state was unavailable. Reconcile before retrying.");
    }

    private void MarkActiveItem(RemoteMarketBatchItemStatus status)
    {
        var active = batchItems.FirstOrDefault(item => item.Status == RemoteMarketBatchItemStatus.Sending);
        if (active is not null)
            active.Status = status;
    }

    private void ResolveIndeterminate(RemoteMarketPurchaseAttempt pending)
    {
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
        Complete(pending, "No purchase confirmation arrived before the deadline. The server silently drops invalid requests, so re-search the item, then reconcile inventory and gil before retrying.");
    }

    private void Fail(RemoteMarketPurchaseAttempt pending, string reason)
    {
        pending.Phase = RemoteMarketPurchasePhase.Failed;
        Complete(pending, reason);
    }

    private void Complete(RemoteMarketPurchaseAttempt pending, string message)
    {
        if (pending.SentAtUtc is not null)
            ClearStagedPurchase();
        pending.FailureReason = message;
        attempt = null;
        log.Information(
            "[MarketMafioso] Remote market purchase {Phase}. ListingId={ListingId} PacketObserved={PacketObserved} PacketMatchesIntent={PacketMatchesIntent} Message={Message}",
            pending.Phase,
            pending.Selection.ListingId,
            pending.PacketObserved,
            pending.PacketMatchesIntent,
            message);
        WriteEvidence(pending);
        framework.RunOnTick(AdvanceBatch, BatchPacingDelay);
    }

    private void NoteRejectedTerritory()
    {
        var territory = clientState.TerritoryType;
        if (configuration.RemoteMarketRejectedTerritories.Contains(territory))
            return;
        configuration.RemoteMarketRejectedTerritories.Add(territory);
        configuration.Save();
        log.Information("[MarketMafioso] Remote market recorded territory {Territory} as purchase-rejecting.", territory);
    }

    private void ClearStagedPurchase()
    {
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null)
                proxy->LastPurchasedMarketboardItem.ListingId = 0;
        }
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

    private static RemoteMarketSelectionView ToSelection(MarketBoardListing listing, string itemName) => new(
        (int)listing.ListingId,
        listing.ItemId,
        itemName,
        listing.IsHqItem,
        listing.Quantity,
        listing.UnitPrice,
        listing.TotalTax,
        (listing.UnitPrice * (ulong)listing.Quantity) + listing.TotalTax,
        listing.ListingId,
        listing.RetainerId);

    private static unsafe MarketBoardListing? FindListing(InfoProxyItemSearch* proxy, ulong listingId)
    {
        var count = (int)Math.Min(proxy->ListingCount, 100);
        for (var index = 0; index < count; index++)
        {
            var listing = proxy->Listings[index];
            if (listing.ListingId == listingId)
                return listing;
        }
        return null;
    }

    private unsafe InfoProxyItemSearch* GetItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null ? null : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
    }

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
        anchor = new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true) + 8f, addon->Y + 4f);
        return true;
    }

    public unsafe bool TryGetResultBounds(out System.Numerics.Vector2 anchor, out float maxHeight)
    {
        anchor = default;
        maxHeight = 0f;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        if (addon == null || !addon->IsVisible)
            return false;
        anchor = new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true) + 8f, addon->Y + 4f);
        maxHeight = Math.Max(0f, addon->GetScaledHeight(true) - 8f);
        return true;
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

internal enum RemoteMarketBatchItemStatus
{
    Queued,
    Sending,
    Confirmed,
    Failed,
    Skipped,
}

internal sealed class RemoteMarketBatchItem(ulong listingId, RemoteMarketSelectionView selection, RemoteMarketBatchItemStatus status)
{
    public ulong ListingId { get; } = listingId;
    public RemoteMarketSelectionView Selection { get; } = selection;
    public RemoteMarketBatchItemStatus Status { get; set; } = status;
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

internal sealed record RemoteMarketListingView(
    ulong ListingId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    byte MateriaCount,
    bool AlreadyPurchased,
    RemoteMarketBatchItemStatus? BatchStatus);

internal sealed record RemoteMarketBatchView(
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    bool Active);

internal sealed record RemoteMarketView(
    bool Available,
    IReadOnlyList<RemoteMarketListingView> Listings,
    RemoteMarketBatchView? Batch,
    string? LastOutcome,
    string? ContextBlockReason,
    uint? GilOnHand);

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
