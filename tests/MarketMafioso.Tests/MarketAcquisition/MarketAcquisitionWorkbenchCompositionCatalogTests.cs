using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionWorkbenchCompositionCatalogTests
{
    [Fact]
    public void SaveNew_PersistsDeepCopyAndRejectsDuplicateName()
    {
        var store = new MemoryStore();
        var catalog = new MarketAcquisitionWorkbenchCompositionCatalog(store);
        var document = CreateDocument();
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        var saved = catalog.SaveNew("Shard restock", document, now);
        document.Lines[0] = document.Lines[0] with { ItemName = "Changed later" };
        var duplicate = catalog.SaveNew("shard RESTOCK", CreateDocument(), now.AddMinutes(1));

        Assert.True(saved.Success);
        Assert.False(duplicate.Success);
        Assert.Single(catalog.Compositions);
        Assert.Equal("Fire Shard", catalog.Compositions[0].Lines[0].ItemName);
        Assert.Equal(saved.Composition?.Id, catalog.SelectedCompositionId);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void UpdateRenameDuplicateDelete_ManageSelectedCompositionLifecycle()
    {
        var store = new MemoryStore();
        var catalog = new MarketAcquisitionWorkbenchCompositionCatalog(store);
        var initial = catalog.SaveNew("Daily supplies", CreateDocument(), DateTimeOffset.UtcNow).Composition!;
        var updatedDocument = CreateDocument() with
        {
            Lines =
            [
                new() { ItemId = 2, ItemName = "Fire Shard" },
                new() { ItemId = 3, ItemName = "Ice Shard" },
            ],
        };

        Assert.True(catalog.UpdateSelected(updatedDocument).Success);
        Assert.True(catalog.RenameSelected("Daily shards").Success);
        var duplicate = catalog.DuplicateSelected();

        Assert.True(duplicate.Success);
        Assert.Equal("Daily shards copy", duplicate.Composition?.Name);
        Assert.Equal(2, catalog.Compositions.Count);
        Assert.Equal(duplicate.Composition?.Id, catalog.SelectedCompositionId);
        Assert.True(catalog.DeleteSelected().Success);
        Assert.Single(catalog.Compositions);
        Assert.Equal(initial.Id, catalog.SelectedCompositionId);
    }

    [Fact]
    public void ConfigurationStore_RoundTripsCompositionAndSelection()
    {
        var saves = 0;
        var config = new Configuration();
        var store = new ConfigurationMarketAcquisitionWorkbenchCompositionStore(config, () => saves++);
        var composition = MarketAcquisitionWorkbenchComposition.FromDocument(
            "Raid food",
            CreateDocument() with { WorldMode = "AllWorldSweep", SweepScope = "Region" },
            new DateTimeOffset(2026, 7, 15, 13, 0, 0, TimeSpan.Zero));

        store.Save(new MarketAcquisitionWorkbenchCompositionStoreSnapshot([composition], composition.Id));
        var restored = store.Load();

        Assert.Equal(1, saves);
        Assert.Equal(composition.Id, restored.SelectedCompositionId);
        var value = Assert.Single(restored.Compositions);
        Assert.Equal("Raid food", value.Name);
        Assert.Equal("AllWorldSweep", value.WorldMode);
        Assert.Equal([2u], value.Lines.Select(line => line.ItemId));
        Assert.NotSame(composition.Lines[0], value.Lines[0]);
    }

    private static MarketAcquisitionRequestDocument CreateDocument() =>
        MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Siren") with
        {
            Lines = [new() { ItemId = 2, ItemName = "Fire Shard", TargetQuantity = 99 }],
        };

    private sealed class MemoryStore : IMarketAcquisitionWorkbenchCompositionStore
    {
        private MarketAcquisitionWorkbenchCompositionStoreSnapshot snapshot = new([], null);

        public int SaveCount { get; private set; }

        public MarketAcquisitionWorkbenchCompositionStoreSnapshot Load() => snapshot;

        public void Save(MarketAcquisitionWorkbenchCompositionStoreSnapshot value)
        {
            snapshot = value;
            SaveCount++;
        }
    }
}
