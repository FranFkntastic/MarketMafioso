using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests;

public unsafe class TradeOfferedInventoryTests
{
    [Fact]
    public void ReadsSymbolicOfferQuantitiesWithoutResolvingToSourceStacks()
    {
        // These getters model a source stack of 999 items / 72,614,757 gil.
        // The offer contains only 3 items / 1,000,000 gil.
        var vtable = stackalloc nint[17];
        vtable[5] = (nint)(delegate* unmanaged<InventoryItem*, uint>)&GetBaseItemId;
        vtable[12] = (nint)(delegate* unmanaged<InventoryItem*, uint>)&GetSourceQuantity;
        vtable[16] = (nint)(delegate* unmanaged<InventoryItem*, InventoryItem.ItemFlags>)&GetFlags;
        var slots = stackalloc InventoryItem[12];
        for (var i = 0; i < 12; i++)
        {
            slots[i] = default;
            *(nint**)(slots + i) = vtable;
        }
        slots[0].IsSymbolic = true;
        slots[0].LinkedInventoryType = (ushort)InventoryType.Inventory1;
        slots[0].LinkedItemSlot = 7;
        slots[0].Quantity = 3;
        slots[5].IsSymbolic = true;
        slots[5].LinkedInventoryType = (ushort)InventoryType.Currency;
        slots[5].Quantity = 1_000_000;
        slots[6].Quantity = 999;
        slots[11].LinkedInventoryType = (ushort)InventoryType.Currency;
        slots[11].Quantity = 72_614_757;
        var containerVtable = stackalloc nint[6];
        containerVtable[5] = (nint)(delegate* unmanaged<InventoryContainer*, int, InventoryItem*>)&GetSourceSlot;
        var container = new InventoryContainer { Items = slots, Size = 6, IsLoaded = true };
        *(nint**)&container = containerVtable;

        var offer = DalamudTradeQueueIo.ReadOfferedInventory(&container, 123, true);

        Assert.NotNull(offer);
        Assert.Equal(123u, offer.PartnerEntityId);
        Assert.True(offer.SyncPending);
        Assert.Equal(1_000_000, offer.Gil);
        Assert.Equal(new TradeOfferedItem(42, true, 3, (uint)InventoryType.Inventory1, 7), Assert.Single(offer.Items));
    }

    [Fact]
    public void MissingSlotStorageIsUnavailableRatherThanAnEmptyOffer()
    {
        var container = new InventoryContainer { Size = 6, IsLoaded = true };
        Assert.Null(DalamudTradeQueueIo.ReadOfferedInventory(&container, 123, false));
        Assert.Null(DalamudTradeQueueIo.ReadOfferedInventory(null, 123, false));
    }

    [UnmanagedCallersOnly]
    private static InventoryItem* GetSourceSlot(InventoryContainer* container, int index) =>
        container->Items + 6 + index;

    [UnmanagedCallersOnly]
    private static uint GetBaseItemId(InventoryItem* item) => 42;

    [UnmanagedCallersOnly]
    private static uint GetSourceQuantity(InventoryItem* item) =>
        item->LinkedInventoryType == (ushort)InventoryType.Currency ? 72_614_757u : 999u;

    [UnmanagedCallersOnly]
    private static InventoryItem.ItemFlags GetFlags(InventoryItem* item) => InventoryItem.ItemFlags.HighQuality;
}
