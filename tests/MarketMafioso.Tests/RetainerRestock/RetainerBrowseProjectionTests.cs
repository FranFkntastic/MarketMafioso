using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerBrowseProjectionTests
{
    private static readonly RetainerOwnerScope CurrentOwner = new("Current Character", "Maduin");

    [Fact]
    public void Build_ProjectsOnlyPlayerAndOwnerScopedRetainersWithStableDuplicateNameScopes()
    {
        var first = Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var second = Retainer(11, "Eris", (100, "Darksteel Ore", 4), (200, "Spruce Log", 5));
        var other = Retainer(12, "Eris", (100, "Darksteel Ore", 99), ownerName: "Other Character", homeWorld: CurrentOwner.HomeWorld);
        var legacy = Retainer(13, "Legacy", (100, "Darksteel Ore", 99), ownerName: null, homeWorld: null);
        first.MarketListings.Add(Listing(100, "Darksteel Ore", 2, 10));
        second.MarketListings.Add(Listing(200, "Spruce Log", 1, 20));
        other.MarketListings.Add(Listing(100, "Darksteel Ore", 99, 1));
        var config = Config(first, second, other, legacy);

        var projection = RetainerRestockStockCatalog.BuildBrowseProjection(
            [Bag((100, "Darksteel Ore", 2))], config, CurrentOwner);

        Assert.Equal(
            ["all", "player", "retainer:10", "retainer:11"],
            projection.Scopes.Select(scope => scope.Key).ToArray());
        Assert.Equal("Eris", projection.Scopes.Single(scope => scope.Key == "retainer:10").DisplayName);
        Assert.Equal("Eris", projection.Scopes.Single(scope => scope.Key == "retainer:11").DisplayName);

        var ore = projection.ItemGroups.Single(group => group.ItemId == 100);
        Assert.Equal(9, ore.TotalQuantity);
        Assert.Equal(2, ore.PlayerQuantity);
        Assert.Equal(7, ore.RetainerQuantity);
        Assert.Equal([10UL, 11UL], ore.Stacks.Where(stack => stack.RetainerId is not null).Select(stack => stack.RetainerId!.Value).ToArray());
        Assert.Equal(3, projection.GetItemGroups("retainer:10").Single().TotalQuantity);
        Assert.Single(projection.Listings, listing => listing.RetainerId == 10);
        Assert.Single(projection.Listings, listing => listing.RetainerId == 11);
    }

    [Fact]
    public void Build_WithoutAnAvailableOwnerScope_DoesNotWidenToCachedRetainers()
    {
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection(
            [Bag((100, "Darksteel Ore", 2))],
            Config(Retainer(10, "Eris", (100, "Darksteel Ore", 3))),
            new RetainerOwnerScope(null, null));

        Assert.Equal(["all", "player"], projection.Scopes.Select(scope => scope.Key).ToArray());
        var group = Assert.Single(projection.ItemGroups);
        Assert.Equal(2, group.PlayerQuantity);
        Assert.Equal(0, group.RetainerQuantity);
        Assert.Empty(projection.Listings);
    }

    [Fact]
    public void ItemGroups_PreservePhysicalStackChildrenAndOnlyRetainerStockCanStageWithdrawal()
    {
        var retainer = Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection(
            [
                Bag((100, "Darksteel Ore", 2), (200, "Cobalt Ingot", 8)),
                Bag((100, "Darksteel Ore", 5)),
            ],
            Config(retainer),
            CurrentOwner);

        var ore = projection.ItemGroups.Single(group => group.ItemId == 100);
        var cobalt = projection.ItemGroups.Single(group => group.ItemId == 200);
        Assert.Equal(3, ore.Stacks.Count);
        Assert.True(ore.CanWithdrawToPlayer);
        Assert.False(cobalt.CanWithdrawToPlayer);

        var plan = new List<RetainerRestockPlanItem>();
        Assert.False(RetainerBrowseWithdrawalPlanStager.TryUpsert(plan, cobalt, 20));
        Assert.True(RetainerBrowseWithdrawalPlanStager.TryUpsert(plan, ore, 20));
        Assert.True(RetainerBrowseWithdrawalPlanStager.TryUpsert(plan, ore, 40));
        var item = Assert.Single(plan);
        Assert.Equal(100U, item.ItemId);
        Assert.Equal(40, item.DesiredPlayerQuantity);
        Assert.True(item.Enabled);
    }

    [Fact]
    public void ListingEvidence_PreservesKnownZeroSeparatelyFromUnknownPriceAndCondition()
    {
        var retainer = Retainer(10, "Eris");
        retainer.MarketListings.Add(Listing(100, "Known Zero", 2, unitPrice: 0, conditionPercent: 0));
        retainer.MarketListings.Add(Listing(101, "Unknown Evidence", 2, unitPrice: null, conditionPercent: null, legacyCondition: 0));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection([], Config(retainer), CurrentOwner);

        var known = projection.Listings.Single(listing => listing.ItemId == 100);
        var unknown = projection.Listings.Single(listing => listing.ItemId == 101);
        Assert.True(known.UnitPrice.IsKnown);
        Assert.Equal(0m, known.UnitPrice.Value);
        Assert.True(known.TotalPrice.IsKnown);
        Assert.Equal(0m, known.TotalPrice.Value);
        Assert.True(known.Condition.IsKnown);
        Assert.Equal(0m, known.Condition.Value);
        Assert.False(unknown.UnitPrice.IsKnown);
        Assert.False(unknown.TotalPrice.IsKnown);
        Assert.False(unknown.Condition.IsKnown);

        var controller = new RetainerBrowseQueryController();
        Assert.Equal([100U], controller.QueryListings(projection, "price=0").Listings.Select(listing => listing.ItemId).ToArray());
        Assert.Equal([100U], controller.QueryListings(projection, "condition=0").Listings.Select(listing => listing.ItemId).ToArray());
        Assert.Equal([100U], controller.QueryListings(projection, "totalPrice=0").Listings.Select(listing => listing.ItemId).ToArray());
    }

    [Fact]
    public void QueryContexts_BindTheApprovedItemsAndListingFields()
    {
        var first = Retainer(10, "Eris", (100, "Darksteel Ore", 3), (200, "Spruce Log", 1));
        var second = Retainer(11, "Bryn", (100, "Darksteel Ore", 4));
        first.MarketListings.Add(Listing(100, "Darksteel Ore", 2, 40, isHq: true, conditionPercent: 75));
        second.MarketListings.Add(Listing(200, "Spruce Log", 1, 5, isHq: false, conditionPercent: 50));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection([Bag((100, "Darksteel Ore", 2))], Config(first, second), CurrentOwner);
        var controller = new RetainerBrowseQueryController();

        Assert.Equal([100U], controller.QueryItems(projection, "darksteel ownership.owned:true ownership.quantity>=9 ownership.retainer:Eris").Items.Select(group => group.ItemId).ToArray());
        Assert.Equal([100U], controller.QueryListings(projection, "quality:hq offer.source:market price>=40 totalPrice>=80 offer.quantity>=2 retainer:Eris").Listings.Select(listing => listing.ItemId).ToArray());
    }

    [Fact]
    public void Listings_IntentionallyExposeNoAgeFieldAndProvideReferenceAndCompletion()
    {
        var retainer = Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        retainer.MarketListings.Add(Listing(100, "Darksteel Ore", 2, 40, isHq: true, conditionPercent: 75));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection([], Config(retainer), CurrentOwner);
        var controller = new RetainerBrowseQueryController();

        var invalid = controller.QueryListings(projection, "offer.age<1h");
        Assert.False(invalid.Filter.IsValid);
        Assert.False(invalid.Filter.IsShowingLastValidResults);
        Assert.Empty(invalid.Listings);
        var reference = controller.GetReference(projection, RetainerBrowseQueryMode.Listings);
        Assert.False(reference.Fields.Single(field => field.Key == "offer.age").IsAvailable);
        Assert.True(reference.Fields.Single(field => field.Key == "offer.price").IsAvailable);
        var completion = controller.Complete(projection, RetainerBrowseQueryMode.Listings, "is:h", 4);
        Assert.Contains(completion.Items, item => item.InsertionText.Equals("hq", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QueryController_ReusesCompilationAndResultsUntilDeterministicDataOrContextInputsChange()
    {
        var retainer = Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var initial = RetainerRestockStockCatalog.BuildBrowseProjection([Bag((100, "Darksteel Ore", 2))], Config(retainer), CurrentOwner);
        var controller = new RetainerBrowseQueryController();

        Assert.Single(controller.QueryItems(initial, "ownership.quantity>=5").Items);
        Assert.Equal(1, controller.ItemCompilationCount);
        Assert.Equal(1, controller.ItemEvaluationCount);
        Assert.Single(controller.QueryItems(initial, "ownership.quantity>=5").Items);
        Assert.Equal(1, controller.ItemCompilationCount);
        Assert.Equal(1, controller.ItemEvaluationCount);

        retainer.Bags.Single().Items.Single().Quantity = 4;
        var changedData = RetainerRestockStockCatalog.BuildBrowseProjection([Bag((100, "Darksteel Ore", 2))], Config(retainer), CurrentOwner);
        Assert.NotEqual(initial.Identity.Data, changedData.Identity.Data);
        Assert.Equal(initial.Identity.Context, changedData.Identity.Context);
        Assert.Single(controller.QueryItems(changedData, "ownership.quantity>=5").Items);
        Assert.Equal(1, controller.ItemCompilationCount);
        Assert.Equal(2, controller.ItemEvaluationCount);

        retainer.RetainerName = "Eris Renamed";
        var changedContext = RetainerRestockStockCatalog.BuildBrowseProjection([Bag((100, "Darksteel Ore", 2))], Config(retainer), CurrentOwner);
        Assert.NotEqual(changedData.Identity.Context, changedContext.Identity.Context);
        Assert.Single(controller.QueryItems(changedContext, "ownership.quantity>=5").Items);
        Assert.Equal(2, controller.ItemCompilationCount);
    }

    [Fact]
    public void QueryController_InvalidEditsKeepTheLastValidCompilationAndResults()
    {
        var retainer = Retainer(10, "Eris", (100, "Darksteel Ore", 3), (200, "Spruce Log", 3));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection([], Config(retainer), CurrentOwner);
        var controller = new RetainerBrowseQueryController();

        Assert.Equal([100U], controller.QueryItems(projection, "darksteel").Items.Select(group => group.ItemId).ToArray());
        var invalid = controller.QueryItems(projection, "ownership.quantity:");

        Assert.False(invalid.Filter.IsValid);
        Assert.True(invalid.Filter.IsShowingLastValidResults);
        Assert.Equal([100U], invalid.Items.Select(group => group.ItemId).ToArray());
        Assert.Equal(2, controller.ItemCompilationCount);
    }

    [Fact]
    public void QueryController_ContextChangeDoesNotLeakLastValidRowsIntoInvalidScope()
    {
        var first = Retainer(10, "Eris", (100, "Darksteel Ore", 3));
        var second = Retainer(11, "Belladonna", (200, "Spruce Log", 4));
        var projection = RetainerRestockStockCatalog.BuildBrowseProjection([], Config(first, second), CurrentOwner);
        var controller = new RetainerBrowseQueryController();

        var firstScope = RetainerBrowseScopeOption.RetainerKey(10);
        var secondScope = RetainerBrowseScopeOption.RetainerKey(11);
        Assert.Equal([100U], controller.QueryItems(projection, "darksteel", firstScope).Items.Select(group => group.ItemId).ToArray());

        var invalid = controller.QueryItems(projection, "ownership.quantity:", secondScope);

        Assert.False(invalid.Filter.IsValid);
        Assert.False(invalid.Filter.IsShowingLastValidResults);
        Assert.Empty(invalid.Items);
        Assert.Same(projection.GetItemGroups(secondScope), projection.GetItemGroups(secondScope));
    }

    private static Configuration Config(params CachedRetainer[] retainers)
    {
        var config = new Configuration();
        foreach (var retainer in retainers)
            config.RetainerCache[retainer.RetainerId] = retainer;
        return config;
    }

    private static InventoryBag Bag(params (uint ItemId, string ItemName, uint Quantity)[] items) => new()
    {
        BagName = "Inventory1",
        Items = items.Select(item => new ItemSlot
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            Quantity = item.Quantity,
        }).ToList(),
    };

    private static CachedRetainer Retainer(
        ulong id,
        string name,
        params (uint ItemId, string ItemName, uint Quantity)[] items) => Retainer(id, name, items, CurrentOwner.CharacterName, CurrentOwner.HomeWorld);

    private static CachedRetainer Retainer(
        ulong id,
        string name,
        (uint ItemId, string ItemName, uint Quantity) item,
        string? ownerName,
        string? homeWorld) => Retainer(id, name, [item], ownerName, homeWorld);

    private static CachedRetainer Retainer(
        ulong id,
        string name,
        (uint ItemId, string ItemName, uint Quantity)[] items,
        string? ownerName,
        string? homeWorld) => new()
    {
        RetainerId = id,
        RetainerName = name,
        OwnerCharacterName = ownerName,
        OwnerHomeWorld = homeWorld,
        Bags =
        [
            new CachedBag
            {
                BagName = "RetainerInventory",
                Items = items.Select(item => new CachedItem
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    Quantity = item.Quantity,
                }).ToList(),
            },
        ],
    };

    private static CachedMarketListing Listing(
        uint itemId,
        string itemName,
        uint quantity,
        uint? unitPrice,
        bool isHq = false,
        float? conditionPercent = null,
        float legacyCondition = 0) => new()
    {
        ItemId = itemId,
        ItemName = itemName,
        Quantity = quantity,
        UnitPrice = unitPrice,
        IsHQ = isHq,
        ConditionPercent = conditionPercent,
        Condition = legacyCondition,
    };
}
