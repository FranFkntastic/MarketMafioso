namespace MarketMafioso.Server.Tests;

public sealed class InventorySnapshotViewBuilderTests
{
    [Fact]
    public void Build_GroupsPlayerAndRetainerInventory()
    {
        var stored = new StoredInventoryReport
        {
            Id = "snapshot-1",
            ReceivedAt = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero),
            Report = new InventoryReport
            {
                Metadata = new InventoryReportMetadata
                {
                    SchemaVersion = 1,
                    SourcePlugin = "MarketMafioso",
                    PluginVersion = "1.0.0.0",
                    GeneratedAtUtc = "2026-06-22T11:58:59.0000000Z",
                },
                CharacterName = "Test Character",
                HomeWorld = "Gilgamesh",
                Timestamp = "2026-06-22T11:59:00.0000000Z",
                PlayerInventory =
                [
                    new InventoryBag
                    {
                        BagName = "Inventory1",
                        Items =
                        [
                            new ItemSlot
                            {
                                ItemId = 2,
                                ItemName = "Fire Shard",
                                Quantity = 120,
                                IsHQ = false,
                                Condition = 0,
                            },
                        ],
                    },
                ],
                Retainers =
                [
                    new RetainerReport
                    {
                        RetainerName = "Sample Retainer",
                        RetainerId = 123456789,
                        LastUpdated = "2026-06-22T11:55:00.0000000Z",
                        Bags =
                        [
                            new InventoryBag
                            {
                                BagName = "RetainerPage1",
                                Items =
                                [
                                    new ItemSlot
                                    {
                                        ItemId = 4,
                                        ItemName = "Lightning Shard",
                                        Quantity = 99,
                                        IsHQ = true,
                                        Condition = 100,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        };

        var view = InventorySnapshotViewBuilder.Build(stored);

        Assert.Equal("snapshot-1", view.Id);
        Assert.Equal(1, view.Metadata.SchemaVersion);
        Assert.Equal("MarketMafioso", view.Metadata.SourcePlugin);
        Assert.Equal("1.0.0.0", view.Metadata.PluginVersion);
        Assert.Equal("2026-06-22T11:58:59.0000000Z", view.Metadata.GeneratedAtUtc);
        Assert.Equal("Test Character", view.CharacterName);
        Assert.Equal("Gilgamesh", view.HomeWorld);
        Assert.Single(view.PlayerInventory.Bags);
        Assert.Single(view.Retainers);

        var playerBag = view.PlayerInventory.Bags[0];
        Assert.Equal("Inventory1", playerBag.Name);
        Assert.Equal(120, playerBag.Quantity);
        Assert.Equal("Fire Shard", playerBag.Items[0].DisplayName);

        var retainer = view.Retainers[0];
        Assert.Equal("Sample Retainer", retainer.Name);
        Assert.Equal((ulong)123456789, retainer.RetainerId);
        Assert.Equal("RetainerPage1", retainer.Bags[0].Name);
        Assert.True(retainer.Bags[0].Items[0].IsHQ);

        Assert.Equal(2, view.Totals.Stacks);
        Assert.Equal(219, view.Totals.Quantity);
        Assert.Equal(1, view.Totals.HqStacks);
        Assert.Equal(1, view.Totals.Retainers);
    }

    [Fact]
    public void Build_PreservesEmptySections()
    {
        var stored = new StoredInventoryReport
        {
            Id = "empty",
            ReceivedAt = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero),
            Report = new InventoryReport
            {
                CharacterName = "Empty Character",
                HomeWorld = "Cactuar",
                Timestamp = "2026-06-22T11:59:00.0000000Z",
            },
        };

        var view = InventorySnapshotViewBuilder.Build(stored);

        Assert.Equal("Empty Character", view.CharacterName);
        Assert.Equal(0, view.Metadata.SchemaVersion);
        Assert.Equal("Unknown", view.Metadata.SourcePlugin);
        Assert.Equal("Unknown", view.Metadata.PluginVersion);
        Assert.Equal("Unknown", view.Metadata.GeneratedAtUtc);
        Assert.Empty(view.PlayerInventory.Bags);
        Assert.Empty(view.Retainers);
        Assert.Equal(0, view.Totals.Stacks);
        Assert.Equal(0, view.Totals.Quantity);
    }
}
