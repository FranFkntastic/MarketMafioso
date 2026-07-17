using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Semantics;
using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.Server;

public static class InventoryBrowserViewBuilder
{
    public static InventoryBrowserView Build(
        StoredInventoryReport? stored,
        string? filter,
        string? scope = null,
        InventoryBrowserMode mode = InventoryBrowserMode.Items,
        int? caretPosition = null)
    {
        var completionExpression = filter ?? string.Empty;
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim();
        if (stored is null)
            return new InventoryBrowserView { Filter = completionExpression, Scope = normalizedScope, Mode = mode };

        var stacks = EnumerateStacks(stored.Report)
            .Where(row => normalizedScope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                          row.OwnerName.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var listings = EnumerateListings(stored).Where(row =>
                normalizedScope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                row.OwnerName.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var vocabulary = FfxivFilterCatalog.Create(BuildResolvers(stacks, listings, stored.Report));

        return mode switch
        {
            InventoryBrowserMode.Stacks => BuildStacksView(stored, completionExpression, completionExpression, caretPosition, normalizedScope, vocabulary, stacks),
            InventoryBrowserMode.Listings => BuildListingsView(stored, completionExpression, completionExpression, caretPosition, normalizedScope, vocabulary, listings),
            _ => BuildItemsView(stored, completionExpression, completionExpression, caretPosition, normalizedScope, vocabulary, stacks),
        };
    }

    private static InventoryBrowserView BuildItemsView(
        StoredInventoryReport stored,
        string filter,
        string completionExpression,
        int? caretPosition,
        string scope,
        FfxivFilterCatalog vocabulary,
        IReadOnlyList<StackRecord> source)
    {
        var context = new FilterContextBuilder<StackRecord>(vocabulary.Catalog)
            .Bind(vocabulary.ItemName, row => Evidence.Known(row.ItemKey))
            .Bind(vocabulary.InstanceQuality, row => Evidence.Known(row.Quality))
            .Bind(vocabulary.InstanceQuantity, row => Evidence.Known((long)row.Quantity))
            .Bind(vocabulary.InstanceLocation, row => Evidence.Known(row.Location))
            .Bind(vocabulary.InstanceEquipped, row => row.Equipped)
            .Bind(vocabulary.InstanceCondition, row => row.Condition)
            .BindSet(vocabulary.OwnershipRetainers, row => Evidence.Known(row.RetainerKeys))
            .UseDefaultText(vocabulary.ItemName, row => Evidence.Known(row.DisplayName))
            .Build("ffxiv.grouped-inventory", "2");
        var compilation = FilterCompiler.Compile(filter, context);
        var matchingStacks = compilation.IsValid ? source.Where(compilation.Matches).ToArray() : [];
        var items = AggregateItems(matchingStacks).Select(ToItemView).ToArray();
        var stacks = matchingStacks.Select(ToStackView).ToArray();

        return CreateBase(stored, filter, completionExpression, caretPosition, scope, InventoryBrowserMode.Items, compilation, context) with
        {
            Items = items,
            Stacks = stacks,
            MatchingRecordCount = items.Length,
            TotalQuantity = checked((int)stacks.Sum(row => (long)row.Quantity)),
            HqQuantity = checked((int)stacks.Where(row => row.IsHq).Sum(row => (long)row.Quantity)),
            OwnerCount = stacks.Select(row => row.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ItemTypeKnownCount = items.Count(row => !string.IsNullOrWhiteSpace(row.ItemType)),
        };
    }

    private static InventoryBrowserView BuildStacksView(
        StoredInventoryReport stored,
        string filter,
        string completionExpression,
        int? caretPosition,
        string scope,
        FfxivFilterCatalog vocabulary,
        IReadOnlyList<StackRecord> source)
    {
        var context = new FilterContextBuilder<StackRecord>(vocabulary.Catalog)
            .Bind(vocabulary.ItemName, row => Evidence.Known(row.ItemKey))
            .Bind(vocabulary.InstanceQuality, row => Evidence.Known(row.Quality))
            .Bind(vocabulary.InstanceQuantity, row => Evidence.Known((long)row.Quantity))
            .Bind(vocabulary.InstanceLocation, row => Evidence.Known(row.Location))
            .Bind(vocabulary.InstanceEquipped, row => row.Equipped)
            .Bind(vocabulary.InstanceCondition, row => row.Condition)
            .BindSet(vocabulary.OwnershipRetainers, row => Evidence.Known(row.RetainerKeys))
            .UseDefaultText(vocabulary.ItemName, row => Evidence.Known(row.DisplayName))
            .Build("ffxiv.item-instances", "1");
        var compilation = FilterCompiler.Compile(filter, context);
        var records = compilation.IsValid ? source.Where(compilation.Matches).ToArray() : [];
        var stacks = records.Select(ToStackView).ToArray();

        return CreateBase(stored, filter, completionExpression, caretPosition, scope, InventoryBrowserMode.Stacks, compilation, context) with
        {
            Stacks = stacks,
            MatchingRecordCount = stacks.Length,
            TotalQuantity = checked((int)stacks.Sum(row => (long)row.Quantity)),
            HqQuantity = checked((int)stacks.Where(row => row.IsHq).Sum(row => (long)row.Quantity)),
            OwnerCount = stacks.Select(row => row.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ItemTypeKnownCount = stacks.Count(row => !string.IsNullOrWhiteSpace(row.ItemType)),
        };
    }

    private static InventoryBrowserView BuildListingsView(
        StoredInventoryReport stored,
        string filter,
        string completionExpression,
        int? caretPosition,
        string scope,
        FfxivFilterCatalog vocabulary,
        IReadOnlyList<ListingRecord> source)
    {
        var context = new FilterContextBuilder<ListingRecord>(vocabulary.Catalog)
            .Bind(vocabulary.ItemName, row => Evidence.Known(row.ItemKey))
            .Bind(vocabulary.InstanceQuality, row => Evidence.Known(row.Quality))
            .Bind(vocabulary.InstanceCondition, row => row.Condition)
            .Bind(vocabulary.OfferSource, _ => Evidence.Known(FfxivOfferSource.Market))
            .Bind(vocabulary.OfferPrice, row => row.UnitPrice)
            .Bind(vocabulary.OfferTotalPrice, row => row.TotalPrice)
            .Bind(vocabulary.OfferQuantity, row => Evidence.Known((long)row.Quantity))
            .Bind(vocabulary.OfferAge, row => row.Age)
            .BindSet(vocabulary.OwnershipRetainers, row => Evidence.Known<IReadOnlyCollection<FfxivRetainerKey>>([row.RetainerKey]))
            .UseDefaultText(vocabulary.ItemName, row => Evidence.Known(row.DisplayName))
            .Build("ffxiv.purchase-offers", "1");
        var compilation = FilterCompiler.Compile(filter, context);
        var records = compilation.IsValid ? source.Where(compilation.Matches).ToArray() : [];
        var listings = records.Select(ToListingView).ToArray();

        return CreateBase(stored, filter, completionExpression, caretPosition, scope, InventoryBrowserMode.Listings, compilation, context) with
        {
            MarketListings = listings,
            MatchingRecordCount = listings.Length,
            TotalQuantity = checked((int)listings.Sum(row => (long)row.Quantity)),
            HqQuantity = checked((int)listings.Sum(row => (long)row.HqQuantity)),
            OwnerCount = listings.Select(row => row.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ItemTypeKnownCount = listings.Count(row => !string.IsNullOrWhiteSpace(row.ItemType)),
            ListingPriceKnownCount = listings.Count(row => row.UnitPrice is not null),
        };
    }

    private static InventoryBrowserView CreateBase<TRecord>(
        StoredInventoryReport stored,
        string filter,
        string completionExpression,
        int? caretPosition,
        string scope,
        InventoryBrowserMode mode,
        FilterCompilation<TRecord> compilation,
        FilterContext<TRecord> context) => new()
        {
            SnapshotId = stored.Id,
            ReceivedAt = stored.ReceivedAt,
            CharacterName = stored.Report.CharacterName,
            HomeWorld = stored.Report.HomeWorld,
            Filter = filter,
            NormalizedFilter = compilation.NormalizedExpression,
            SemanticFilter = compilation.SemanticExpression,
            FilterValid = compilation.IsValid,
            FilterDiagnostics = compilation.Diagnostics,
            FilterReference = CreateContextReference(context),
            FilterCompletions = FilterCompletionService.Complete(
                context,
                new FilterCompletionRequest(
                    context.ContextId,
                    completionExpression,
                    Math.Clamp(caretPosition ?? completionExpression.Length, 0, completionExpression.Length))).Items,
            Mode = mode,
            Scope = scope,
            Scopes = BuildScopes(stored.Report),
            PlayerGil = stored.Report.PlayerGil,
            RetainerGil = GetRetainerGil(stored.Report),
            TotalGil = stored.Report.PlayerGil is { } playerGil
                ? checked(playerGil + GetRetainerGil(stored.Report))
                : null,
        };

    private static FilterReferenceModel CreateContextReference<TRecord>(FilterContext<TRecord> context)
    {
        var reference = FilterReferenceGenerator.Create(context);
        return reference with
        {
            Fields = reference.Fields.Select(field =>
                field.ValueKind is FilterValueKind.Named or FilterValueKind.Set
                    ? field with { Values = [] }
                    : field).ToArray(),
        };
    }

    private static FfxivFilterResolvers BuildResolvers(
        IReadOnlyList<StackRecord> stacks,
        IReadOnlyList<ListingRecord> listings,
        InventoryReport report)
    {
        var items = stacks.Select(row => new { row.ItemKey, row.DisplayName })
            .Concat(listings.Select(row => new { row.ItemKey, row.DisplayName }))
            .GroupBy(row => row.ItemKey)
            .Select(group => new FilterLiteralCandidate<FfxivItemKey>(group.Key, group.First().DisplayName))
            .ToArray();
        var retainers = report.Retainers
            .Where(retainer => retainer.RetainerId != 0 && !string.IsNullOrWhiteSpace(retainer.RetainerName))
            .GroupBy(retainer => retainer.RetainerId)
            .Select(group => new FilterLiteralCandidate<FfxivRetainerKey>(new(group.Key), group.First().RetainerName))
            .ToArray();
        return new FfxivFilterResolvers(
            new FilterNamedValueCatalog<FfxivItemKey>(items),
            EmptyResolver<FfxivJobKey>(),
            EmptyResolver<FfxivUiCategoryKey>(),
            EmptyResolver<FfxivCharacterKey>(),
            new FilterNamedValueCatalog<FfxivRetainerKey>(retainers),
            EmptyResolver<FfxivWorldKey>(),
            EmptyResolver<FfxivDataCenterKey>());
    }

    private static FilterNamedValueCatalog<T> EmptyResolver<T>() => new([]);

    private static IReadOnlyList<ItemRecord> AggregateItems(IEnumerable<StackRecord> source) => source
        .GroupBy(row => row.ItemKey)
        .Select(group =>
        {
            var first = group.First();
            return new ItemRecord(
                group.Key,
                first.DisplayName,
                group.Select(row => row.ItemType).FirstOrDefault(itemType => !string.IsNullOrWhiteSpace(itemType)),
                checked((int)group.Sum(row => (long)row.Quantity)),
                checked((int)group.Where(row => row.Quality == FfxivItemQuality.HQ).Sum(row => (long)row.Quantity)),
                group.GroupBy(row => new { row.OwnerName, row.OwnerCharacterName, row.OwnerHomeWorld, row.Location, row.BagName })
                    .Select(location => new LocationRecord(
                        location.Key.OwnerName,
                        location.Key.OwnerCharacterName,
                        location.Key.OwnerHomeWorld,
                        location.Key.Location,
                        location.Key.BagName,
                        checked((int)location.Sum(row => (long)row.Quantity)),
                        checked((int)location.Where(row => row.Quality == FfxivItemQuality.HQ).Sum(row => (long)row.Quantity))))
                    .OrderByDescending(row => row.Quantity)
                    .ThenBy(row => row.OwnerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.BagName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                group.SelectMany(row => row.RetainerKeys).Distinct().ToArray());
        })
        .OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.ItemKey.RowId)
        .ToArray();

    private static IEnumerable<StackRecord> EnumerateStacks(InventoryReport report)
    {
        foreach (var bag in report.PlayerInventory)
            foreach (var item in bag.Items)
                yield return CreateStack(report, null, bag, item);

        foreach (var retainer in report.Retainers)
            foreach (var bag in retainer.Bags.Where(bag => !IsNonInventoryRetainerBag(bag.BagName)))
                foreach (var item in bag.Items)
                    yield return CreateStack(report, retainer, bag, item);
    }

    private static StackRecord CreateStack(InventoryReport report, RetainerReport? retainer, InventoryBag bag, ItemSlot item)
    {
        var location = ResolveLocation(bag.Location, bag.BagName, retainer is not null);
        var condition = ResolveCondition(item.ConditionPercent, item.Condition);
        var equipped = item.Equipped is { } isEquipped
            ? Evidence.Known(isEquipped)
            : Evidence.Known(location == FfxivStorageLocation.Equipped);
        var retainerKeys = retainer is { RetainerId: not 0 }
            ? (IReadOnlyCollection<FfxivRetainerKey>)[new(retainer.RetainerId)]
            : [];
        return new StackRecord(
            new FfxivItemKey(item.ItemId),
            DisplayName(item.ItemId, item.ItemName),
            item.ItemType,
            retainer?.RetainerName ?? "Player Inventory",
            retainer is null ? report.CharacterName : ResolveRetainerOwnerCharacterName(report, retainer),
            retainer is null ? report.HomeWorld : ResolveRetainerOwnerHomeWorld(report, retainer),
            item.ContainerKey ?? bag.BagName,
            item.SlotIndex,
            checked((int)item.Quantity),
            item.IsHQ ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
            location,
            equipped,
            condition,
            retainerKeys);
    }

    private static IEnumerable<ListingRecord> EnumerateListings(StoredInventoryReport stored)
    {
        foreach (var retainer in stored.Report.Retainers)
            foreach (var listing in retainer.MarketListings)
            {
                var listedAtText = listing.ListedAt ?? retainer.LastUpdated;
                var age = DateTimeOffset.TryParse(listedAtText, out var listedAt)
                    ? Evidence.Known(stored.ReceivedAt > listedAt ? stored.ReceivedAt - listedAt : TimeSpan.Zero)
                    : Evidence.Unknown<TimeSpan>("The listing observation time was not recorded.");
                var price = listing.UnitPrice is { } unitPrice
                    ? Evidence.Known((decimal)unitPrice)
                    : Evidence.Unknown<decimal>("The listing unit price was not recorded.");
                var total = listing.UnitPrice is { } priceValue
                    ? Evidence.Known((decimal)((ulong)priceValue * listing.Quantity))
                    : Evidence.Unknown<decimal>("The listing total cannot be calculated without a unit price.");
                var condition = ResolveCondition(listing.ConditionPercent, listing.Condition);
                yield return new ListingRecord(
                    new FfxivItemKey(listing.ItemId),
                    DisplayName(listing.ItemId, listing.ItemName),
                    listing.ItemType,
                    retainer.RetainerName,
                    ResolveRetainerOwnerCharacterName(stored.Report, retainer),
                    ResolveRetainerOwnerHomeWorld(stored.Report, retainer),
                    new FfxivRetainerKey(retainer.RetainerId),
                    checked((int)listing.Quantity),
                    listing.IsHQ ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
                    condition,
                    price,
                    total,
                    age,
                    listedAtText);
            }
    }

    private static InventoryBrowserItemView ToItemView(ItemRecord row) => new()
    {
        ItemId = row.ItemKey.RowId,
        DisplayName = row.DisplayName,
        ItemType = row.ItemType,
        TotalQuantity = row.TotalQuantity,
        HqQuantity = row.HqQuantity,
        OwnerCount = row.Locations.Select(location => location.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        Locations = row.Locations.Select(location => new InventoryBrowserLocationView
        {
            OwnerName = location.OwnerName,
            OwnerCharacterName = location.OwnerCharacterName,
            OwnerHomeWorld = location.OwnerHomeWorld,
            Location = location.Location.ToString(),
            BagName = location.BagName,
            Quantity = location.Quantity,
            HqQuantity = location.HqQuantity,
        }).ToArray(),
    };

    private static InventoryBrowserStackView ToStackView(StackRecord row) => new()
    {
        ItemId = row.ItemKey.RowId,
        DisplayName = row.DisplayName,
        ItemType = row.ItemType,
        OwnerName = row.OwnerName,
        OwnerCharacterName = row.OwnerCharacterName,
        OwnerHomeWorld = row.OwnerHomeWorld,
        BagName = row.BagName,
        SlotIndex = row.SlotIndex,
        Location = row.Location.ToString(),
        Quantity = row.Quantity,
        IsHq = row.Quality == FfxivItemQuality.HQ,
        Equipped = row.Equipped.IsKnown ? row.Equipped.Value : null,
        ConditionPercent = row.Condition.IsKnown ? row.Condition.Value : null,
    };

    private static InventoryBrowserMarketListingView ToListingView(ListingRecord row) => new()
    {
        ItemId = row.ItemKey.RowId,
        DisplayName = row.DisplayName,
        ItemType = row.ItemType,
        OwnerName = row.OwnerName,
        OwnerCharacterName = row.OwnerCharacterName,
        OwnerHomeWorld = row.OwnerHomeWorld,
        Quantity = row.Quantity,
        HqQuantity = row.Quality == FfxivItemQuality.HQ ? row.Quantity : 0,
        ConditionPercent = row.Condition.IsKnown ? row.Condition.Value : null,
        UnitPrice = row.UnitPrice.IsKnown ? checked((uint)row.UnitPrice.Value) : null,
        TotalPrice = row.TotalPrice.IsKnown ? checked((ulong)row.TotalPrice.Value) : null,
        ListedAt = row.ListedAt,
        EvidenceAgeSeconds = row.Age.IsKnown ? row.Age.Value.TotalSeconds : null,
    };

    private static FfxivStorageLocation ResolveLocation(string? location, string bagName, bool isRetainer)
    {
        if (Enum.TryParse<FfxivStorageLocation>(location, ignoreCase: true, out var explicitLocation))
            return explicitLocation;
        if (isRetainer)
            return FfxivStorageLocation.Retainer;
        if (bagName.Contains("Equipped", StringComparison.OrdinalIgnoreCase))
            return FfxivStorageLocation.Equipped;
        if (bagName.Contains("Armor", StringComparison.OrdinalIgnoreCase) || bagName.Contains("Armour", StringComparison.OrdinalIgnoreCase))
            return FfxivStorageLocation.Armoury;
        if (bagName.Contains("Saddle", StringComparison.OrdinalIgnoreCase))
            return FfxivStorageLocation.Saddlebag;
        return FfxivStorageLocation.Inventory;
    }

    private static FieldEvidence<decimal> ResolveCondition(float? conditionPercent, float legacyCondition)
    {
        if (conditionPercent is >= 0 and <= 100)
            return Evidence.Known((decimal)conditionPercent.Value);
        if (conditionPercent is not null)
            return Evidence.Unknown<decimal>("The recorded condition percentage is outside the supported range.");
        if (legacyCondition <= 0)
            return Evidence.Unknown<decimal>("This legacy schema cannot distinguish zero condition from missing evidence.");

        var normalized = legacyCondition <= 1 ? legacyCondition * 100 : legacyCondition;
        return normalized <= 100
            ? Evidence.Known((decimal)normalized)
            : Evidence.Unknown<decimal>("The legacy condition value is outside the supported range.");
    }

    private static string DisplayName(uint itemId, string? itemName) =>
        string.IsNullOrWhiteSpace(itemName) ? $"Item {itemId}" : itemName;

    private static IReadOnlyList<InventoryBrowserScopeView> BuildScopes(InventoryReport report)
    {
        var scopes = new List<InventoryBrowserScopeView>
        {
            new()
            {
                ScopeKey = "Player Inventory", DisplayName = "Player Inventory",
                Description = "Player bags and configured inventory sections",
                StackCount = report.PlayerInventory.SelectMany(bag => bag.Items).Count(), Gil = report.PlayerGil, LastUpdated = report.Timestamp,
            },
        };
        scopes.AddRange(report.Retainers.Select(retainer => new InventoryBrowserScopeView
        {
            ScopeKey = retainer.RetainerName,
            DisplayName = retainer.RetainerName,
            Description = "Retainer inventory",
            OwnerCharacterName = ResolveRetainerOwnerCharacterName(report, retainer),
            OwnerHomeWorld = ResolveRetainerOwnerHomeWorld(report, retainer),
            StackCount = retainer.Bags.Where(bag => !IsNonInventoryRetainerBag(bag.BagName)).SelectMany(bag => bag.Items).Count(),
            Gil = retainer.Gil,
            MarketListingCount = retainer.MarketListings.Count + CountLegacyMarketListings(retainer),
            LastUpdated = retainer.LastUpdated,
        }));
        return scopes;
    }

    private static ulong GetRetainerGil(InventoryReport report) =>
        report.Retainers.Aggregate(0UL, (sum, retainer) => sum + retainer.Gil);

    private static bool IsNonInventoryRetainerBag(string bagName) =>
        bagName.Equals("RetainerGil", StringComparison.OrdinalIgnoreCase) ||
        bagName.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase);

    private static int CountLegacyMarketListings(RetainerReport retainer) => retainer.Bags
        .Where(bag => bag.BagName.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase))
        .SelectMany(bag => bag.Items).Count();

    private static string? ResolveRetainerOwnerCharacterName(InventoryReport report, RetainerReport retainer) =>
        string.IsNullOrWhiteSpace(retainer.OwnerCharacterName) ? report.CharacterName : retainer.OwnerCharacterName;

    private static string? ResolveRetainerOwnerHomeWorld(InventoryReport report, RetainerReport retainer) =>
        string.IsNullOrWhiteSpace(retainer.OwnerHomeWorld) ? report.HomeWorld : retainer.OwnerHomeWorld;

    private sealed record StackRecord(
        FfxivItemKey ItemKey, string DisplayName, string? ItemType, string OwnerName,
        string? OwnerCharacterName, string? OwnerHomeWorld, string BagName, int? SlotIndex, int Quantity,
        FfxivItemQuality Quality, FfxivStorageLocation Location, FieldEvidence<bool> Equipped,
        FieldEvidence<decimal> Condition,
        IReadOnlyCollection<FfxivRetainerKey> RetainerKeys);

    private sealed record LocationRecord(
        string OwnerName, string? OwnerCharacterName, string? OwnerHomeWorld, FfxivStorageLocation Location,
        string BagName, int Quantity, int HqQuantity);

    private sealed record ItemRecord(
        FfxivItemKey ItemKey, string DisplayName, string? ItemType, int TotalQuantity, int HqQuantity,
        IReadOnlyList<LocationRecord> Locations, IReadOnlyCollection<FfxivRetainerKey> Retainers);

    private sealed record ListingRecord(
        FfxivItemKey ItemKey, string DisplayName, string? ItemType, string OwnerName,
        string? OwnerCharacterName, string? OwnerHomeWorld, FfxivRetainerKey RetainerKey, int Quantity,
        FfxivItemQuality Quality, FieldEvidence<decimal> Condition, FieldEvidence<decimal> UnitPrice,
        FieldEvidence<decimal> TotalPrice, FieldEvidence<TimeSpan> Age, string? ListedAt);
}
