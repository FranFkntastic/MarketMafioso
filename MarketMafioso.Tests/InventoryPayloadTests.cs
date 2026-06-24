using System.Text.Json;

namespace MarketMafioso.Tests;

public sealed class InventoryPayloadTests
{
    [Fact]
    public void ItemSlot_SerializesItemTypeWhenPresent()
    {
        var json = JsonSerializer.Serialize(new ItemSlot
        {
            ItemId = 42,
            ItemName = "Darksteel Nugget",
            ItemType = "Stone",
            Quantity = 12,
        });

        Assert.Contains(@"""itemType"":""Stone""", json, StringComparison.Ordinal);
    }
}
