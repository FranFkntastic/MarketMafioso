using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace MarketMafioso.Services;

public sealed class ActiveRetainerCaptureService
{
    private readonly ICondition _condition;
    private readonly IGameInventory _gameInventory;
    private readonly ItemNameResolver _itemNameResolver;

    public ActiveRetainerCaptureService(ICondition condition, IGameInventory gameInventory, ItemNameResolver itemNameResolver)
    {
        _condition = condition;
        _gameInventory = gameInventory;
        _itemNameResolver = itemNameResolver;
    }

    public unsafe ActiveRetainerCaptureResult CaptureActiveRetainer()
    {
        if (!_condition[ConditionFlag.OccupiedSummoningBell])
        {
            return new ActiveRetainerCaptureResult(
                ActiveRetainerCaptureStatus.NotAtBell,
                "You must be interacting with a summoning bell.",
                null);
        }

        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
        {
            return new ActiveRetainerCaptureResult(
                ActiveRetainerCaptureStatus.NoActiveRetainer,
                "Retainer manager is not ready.",
                null);
        }

        var activeRetainer = manager->GetActiveRetainer();
        if (activeRetainer == null || activeRetainer->RetainerId == 0)
        {
            return new ActiveRetainerCaptureResult(
                ActiveRetainerCaptureStatus.NoActiveRetainer,
                "No active retainer selected.",
                null);
        }

        var listings = new List<RetainerListingSnapshot>();
        var marketSlots = _gameInventory.GetInventoryItems(GameInventoryType.RetainerMarket);
        var inventoryManager = InventoryManager.Instance();

        if (inventoryManager == null)
        {
            return new ActiveRetainerCaptureResult(
                ActiveRetainerCaptureStatus.RetainerMarketUnavailable,
                "Inventory manager is unavailable.",
                null);
        }

        foreach (var item in marketSlots)
        {
            if (item.IsEmpty)
            {
                continue;
            }

            var itemId = item.BaseItemId;
            var quantity = item.Quantity;
            if (itemId == 0 || quantity <= 0)
            {
                continue;
            }

            var slot = item.InventorySlot;
            var price = inventoryManager->GetRetainerMarketPrice((short)slot);
            if (price == 0)
            {
                continue;
            }

            var itemName = _itemNameResolver.GetItemName(itemId);

            listings.Add(new RetainerListingSnapshot(
                itemId,
                itemName,
                item.IsHq,
                (uint)quantity,
                price,
                slot));
        }

        listings.Sort((left, right) => left.Slot.CompareTo(right.Slot));

        var snapshot = new RetainerSnapshot(
            activeRetainer->RetainerId,
            activeRetainer->NameString,
            DateTimeOffset.Now,
            listings);

        return new ActiveRetainerCaptureResult(
            ActiveRetainerCaptureStatus.Success,
            listings.Count == 0 ? "Captured active retainer, but no market listings were found." : "Captured active retainer market listings.",
            snapshot);
    }
}

public enum ActiveRetainerCaptureStatus
{
    Success,
    NotAtBell,
    NoActiveRetainer,
    RetainerMarketUnavailable
}

public sealed record ActiveRetainerCaptureResult(
    ActiveRetainerCaptureStatus Status,
    string Message,
    RetainerSnapshot? Snapshot);
