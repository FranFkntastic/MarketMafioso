namespace MarketMafioso.Server.Tests;

public sealed class InventoryBrowserFilterTests
{
    private static readonly StoredInventoryReport Snapshot = new()
    {
        Id = "filter-snapshot",
        ReceivedAt = DateTimeOffset.Parse("2026-07-16T03:00:00Z"),
        Report = new InventoryReport
        {
            CharacterName = "Wei Ning",
            HomeWorld = "Siren",
            Timestamp = "2026-07-16T03:00:00Z",
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "Inventory1",
                    Items =
                    [
                        new ItemSlot { ItemId = 1, ItemName = "Darksteel Ingot", ItemType = "Metal", Quantity = 12, IsHQ = true, Condition = 100 },
                        new ItemSlot { ItemId = 2, ItemName = "Iron Ore", ItemType = "Stone", Quantity = 8, Condition = 0 },
                    ],
                },
            ],
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Belladonna",
                    RetainerId = 99,
                    OwnerCharacterName = "Wei Ning",
                    OwnerHomeWorld = "Siren",
                    LastUpdated = "2026-07-16T02:50:00Z",
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerPage1",
                            Items = [new ItemSlot { ItemId = 1, ItemName = "Darksteel Ingot", ItemType = "Metal", Quantity = 99, Condition = 100 }],
                        },
                    ],
                    MarketListings =
                    [
                        new RetainerMarketListing { ItemId = 1, ItemName = "Darksteel Ingot", ItemType = "Metal", Quantity = 20, UnitPrice = 1_800, ListedAt = "2026-07-16T02:53:00Z", Condition = 100 },
                        new RetainerMarketListing { ItemId = 2, ItemName = "Iron Ore", ItemType = "Stone", Quantity = 50, UnitPrice = null, ListedAt = null, Condition = 0 },
                    ],
                },
            ],
        },
    };

    [Fact]
    public void ItemsMode_FiltersStacksBeforeRegroupingTheirSummaries()
    {
        var view = InventoryBrowserViewBuilder.Build(Snapshot, "darksteel quantity>=90", mode: InventoryBrowserMode.Items);

        Assert.True(view.FilterValid);
        var item = Assert.Single(view.Items);
        Assert.Equal("Darksteel Ingot", item.DisplayName);
        Assert.Equal(99, item.TotalQuantity);
        Assert.Contains(item.Locations, location => location.Location == "Retainer");
        Assert.Equal(1, view.MatchingRecordCount);
        Assert.Single(view.Stacks);
        Assert.Equal(99, view.TotalQuantity);
    }

    [Fact]
    public void ItemsMode_AcceptsFriendlyNegationAndColonComparisons()
    {
        var view = InventoryBrowserViewBuilder.Build(
            Snapshot,
            "darksteel -location:inventory quantity:>20",
            mode: InventoryBrowserMode.Items);

        Assert.True(view.FilterValid);
        var item = Assert.Single(view.Items);
        Assert.Equal(99, item.TotalQuantity);
        Assert.Equal("Retainer", Assert.Single(view.Stacks).Location);
    }

    [Fact]
    public void ItemsMode_LocationExclusionsRemoveStacksAndRecomputeTotals()
    {
        var report = Snapshot.Report with
        {
            Retainers = [],
            PlayerInventory =
            [
                new InventoryBag { BagName = "Inventory1", Items = [new ItemSlot { ItemId = 1, ItemName = "Darksteel Ingot", Quantity = 12 }] },
                new InventoryBag { BagName = "SaddleBag1", Items = [new ItemSlot { ItemId = 1, ItemName = "Darksteel Ingot", Quantity = 20 }] },
                new InventoryBag { BagName = "ArmoryMainHand", Items = [new ItemSlot { ItemId = 1, ItemName = "Darksteel Ingot", Quantity = 1 }] },
            ],
        };

        var view = InventoryBrowserViewBuilder.Build(
            Snapshot with { Report = report },
            "-location:saddlebag -location:armoury",
            mode: InventoryBrowserMode.Items);

        Assert.True(view.FilterValid);
        var item = Assert.Single(view.Items);
        Assert.Equal(12, item.TotalQuantity);
        var stack = Assert.Single(view.Stacks);
        Assert.Equal("Inventory", stack.Location);
        Assert.Equal(12, view.TotalQuantity);
        Assert.Empty(view.MarketListings);
    }

    [Fact]
    public void StacksMode_UsesInstanceQualityLocationAndUnknownEvidence()
    {
        var hq = InventoryBrowserViewBuilder.Build(Snapshot, "quality:HQ location:inventory", mode: InventoryBrowserMode.Stacks);
        var unknown = InventoryBrowserViewBuilder.Build(Snapshot, "unknown(condition)", mode: InventoryBrowserMode.Stacks);

        Assert.Single(hq.Stacks);
        Assert.True(hq.Stacks[0].IsHq);
        Assert.False(hq.Stacks[0].Equipped);
        Assert.Single(unknown.Stacks);
        Assert.Equal("Iron Ore", unknown.Stacks[0].DisplayName);
        Assert.Null(unknown.Stacks[0].ConditionPercent);
    }

    [Fact]
    public void StacksMode_PrefersSchemaV2LocationSlotAndZeroConditionEvidence()
    {
        var report = Snapshot.Report with
        {
            Metadata = new InventoryReportMetadata { SchemaVersion = 2 },
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "LegacyLookingBag",
                    Location = "Equipped",
                    Items =
                    [
                        new ItemSlot
                        {
                            ItemId = 1,
                            ItemName = "Darksteel Ingot",
                            Quantity = 1,
                            ContainerKey = "EquippedItems",
                            SlotIndex = 4,
                            Condition = 0,
                            ConditionPercent = 0,
                            Equipped = true,
                        },
                    ],
                },
            ],
            Retainers = [],
        };

        var view = InventoryBrowserViewBuilder.Build(
            Snapshot with { Report = report },
            "is:equipped and condition:0",
            mode: InventoryBrowserMode.Stacks);

        Assert.True(view.FilterValid);
        var stack = Assert.Single(view.Stacks);
        Assert.Equal("Equipped", stack.Location);
        Assert.Equal("EquippedItems", stack.BagName);
        Assert.Equal(4, stack.SlotIndex);
        Assert.True(stack.Equipped);
        Assert.Equal(0, stack.ConditionPercent);
    }

    [Fact]
    public void ListingsMode_FiltersTypedPriceAgeAndRetainer()
    {
        var view = InventoryBrowserViewBuilder.Build(
            Snapshot,
            "price<2000 age<10m retainer:Belladonna",
            mode: InventoryBrowserMode.Listings);

        var listing = Assert.Single(view.MarketListings);
        Assert.Equal((uint)1_800, listing.UnitPrice);
        Assert.Equal((ulong)36_000, listing.TotalPrice);
        Assert.Equal(420, listing.EvidenceAgeSeconds);
        Assert.Equal(1, view.ListingPriceKnownCount);
    }

    [Fact]
    public void InvalidOrUnavailableField_ReturnsDiagnosticsAndNoPartialResults()
    {
        var unknown = InventoryBrowserViewBuilder.Build(Snapshot, "banana:true", mode: InventoryBrowserMode.Items);
        var unavailable = InventoryBrowserViewBuilder.Build(Snapshot, "price<2000", mode: InventoryBrowserMode.Items);

        Assert.False(unknown.FilterValid);
        Assert.Empty(unknown.Items);
        Assert.Contains(unknown.FilterDiagnostics, diagnostic => diagnostic.Code == "FLT3001");
        Assert.False(unavailable.FilterValid);
        Assert.Contains(unavailable.FilterDiagnostics, diagnostic => diagnostic.Code == "FLT3002");
    }

    [Fact]
    public void NumericItemIds_AreNotAUserFacingSearchLanguage()
    {
        var view = InventoryBrowserViewBuilder.Build(Snapshot, "1", mode: InventoryBrowserMode.Items);

        Assert.True(view.FilterValid);
        Assert.Empty(view.Items);
    }

    [Fact]
    public void FilterReferenceAndCompletionsComeFromTheBoundModeContext()
    {
        var items = InventoryBrowserViewBuilder.Build(Snapshot, "price<1", mode: InventoryBrowserMode.Items);
        var stacks = InventoryBrowserViewBuilder.Build(Snapshot, "quality:h", mode: InventoryBrowserMode.Stacks);

        Assert.NotNull(items.FilterReference);
        Assert.DoesNotContain(items.FilterReference.Fields, field => field.Key == "offer.price" && field.IsAvailable);
        Assert.Contains(items.FilterDiagnostics, diagnostic => diagnostic.Code == "FLT3002");
        Assert.Contains(stacks.FilterCompletions, completion => completion.Label == "HQ");
        Assert.True(stacks.FilterCompletions.Count <= 24);
        Assert.Contains(stacks.FilterReference!.Fields, field => field.Key == "instance.quality" && field.IsAvailable);
        Assert.Equal("quality", stacks.FilterReference.Fields.Single(field => field.Key == "instance.quality").PreferredName);
        Assert.Equal("quantity", stacks.FilterReference.Fields.Single(field => field.Key == "instance.quantity").PreferredName);
        Assert.Empty(stacks.FilterReference.Fields.Single(field => field.Key == "item.name").Values);
    }

    [Fact]
    public void CompletionUsesTheSuppliedCaretAndOffersFieldOperators()
    {
        var view = InventoryBrowserViewBuilder.Build(
            Snapshot,
            "quantity darksteel",
            mode: InventoryBrowserMode.Items,
            caretPosition: 8);

        Assert.Equal([":", "=", "!=", "==", "!==", "<", "<=", ">", ">="],
            view.FilterCompletions.Select(completion => completion.Label));
        Assert.All(view.FilterCompletions, completion =>
        {
            Assert.Equal(Franthropy.Filtering.Completion.FilterCompletionKind.Operator, completion.Kind);
            Assert.Equal(new Franthropy.Filtering.Syntax.TextSpan(8, 0), completion.ReplacementSpan);
        });
    }


    [Fact]
    public void NameQualifier_UsesIntentionalFuzzyMatching()
    {
        var view = InventoryBrowserViewBuilder.Build(Snapshot, "name:darksteel", mode: InventoryBrowserMode.Items);

        Assert.True(view.FilterValid);
        Assert.Equal("Darksteel Ingot", Assert.Single(view.Items).DisplayName);
    }
    [Fact]
    public void FriendlyPredicatesAndExactOperators_AreCompletedAtTheCaret()
    {
        var predicate = InventoryBrowserViewBuilder.Build(Snapshot, "is:h", mode: InventoryBrowserMode.Items, caretPosition: 4);
        var exact = InventoryBrowserViewBuilder.Build(Snapshot, "quantity==", mode: InventoryBrowserMode.Items, caretPosition: 10);

        Assert.Contains(predicate.FilterCompletions, completion => completion.InsertionText == "hq");
        Assert.DoesNotContain(predicate.FilterCompletions, completion => completion.InsertionText == "nq");
        Assert.All(predicate.FilterCompletions, completion => Assert.Equal(new Franthropy.Filtering.Syntax.TextSpan(3, 1), completion.ReplacementSpan));
        Assert.DoesNotContain(exact.FilterCompletions, completion => completion.Kind == Franthropy.Filtering.Completion.FilterCompletionKind.Operator);
    }

    [Fact]
    public void ViewPreservesAuthoredFilterAndExposesSeparateSemanticForm()
    {
        var friendly = InventoryBrowserViewBuilder.Build(Snapshot, "  quality:hq   -location:retainer  ", mode: InventoryBrowserMode.Items);
        var canonical = InventoryBrowserViewBuilder.Build(Snapshot, "instance.quality==HQ AND !instance.location==Retainer", mode: InventoryBrowserMode.Items);

        Assert.Equal("  quality:hq   -location:retainer  ", friendly.Filter);
        Assert.Equal("quality:hq -location:retainer", friendly.NormalizedFilter);
        Assert.Equal(canonical.SemanticFilter, friendly.SemanticFilter);
    }

    [Fact]
    public void CompletionUsesFriendlyShortFieldsAndColonComparators()
    {
        var field = InventoryBrowserViewBuilder.Build(
            Snapshot,
            "-loc",
            mode: InventoryBrowserMode.Items,
            caretPosition: 4);
        var comparison = InventoryBrowserViewBuilder.Build(
            Snapshot,
            "quantity:>",
            mode: InventoryBrowserMode.Items,
            caretPosition: 10);

        var location = Assert.Single(field.FilterCompletions, completion => completion.InsertionText == "location");
        Assert.Equal(new Franthropy.Filtering.Syntax.TextSpan(1, 3), location.ReplacementSpan);
        Assert.Equal([">", ">="], comparison.FilterCompletions.Select(completion => completion.Label));
    }
}
