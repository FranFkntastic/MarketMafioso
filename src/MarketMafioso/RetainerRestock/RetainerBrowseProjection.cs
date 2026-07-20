using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;

namespace MarketMafioso.RetainerRestock;

/// <summary>
/// A stable browser scope. Display names are deliberately not identifiers: two retainers can share a name.
/// </summary>
public sealed record RetainerBrowseScopeOption(
    string Key,
    string DisplayName,
    RetainerBrowseScopeKind Kind,
    ulong? RetainerId)
{
    public const string AllKey = "all";
    public const string PlayerKey = "player";

    public static RetainerBrowseScopeOption All { get; } = new(AllKey, "All accessible stock", RetainerBrowseScopeKind.All, null);
    public static RetainerBrowseScopeOption Player { get; } = new(PlayerKey, "Player", RetainerBrowseScopeKind.Player, null);

    public static string RetainerKey(ulong retainerId) => $"retainer:{retainerId.ToString(CultureInfo.InvariantCulture)}";
}

public enum RetainerBrowseScopeKind
{
    All,
    Player,
    Retainer,
}

/// <summary>One observed physical player or retainer stack contributing to an item group.</summary>
public sealed record RetainerBrowseStockStack(
    string ScopeKey,
    RetainerBrowseScopeKind ScopeKind,
    ulong? RetainerId,
    string OwnerName,
    string StorageName,
    int? SlotIndex,
    uint ItemId,
    string ItemName,
    string? ItemType,
    int Quantity,
    FfxivItemQuality Quality,
    FieldEvidence<decimal> Condition);

/// <summary>
/// A browser item projection. Player-only groups are useful ownership evidence but cannot enter a withdrawal plan.
/// </summary>
public sealed record RetainerBrowseItemGroup(
    uint ItemId,
    string ItemName,
    string? ItemType,
    IReadOnlyList<RetainerBrowseStockStack> Stacks)
{
    public int TotalQuantity => checked((int)Stacks.Sum(stack => (long)stack.Quantity));
    public int PlayerQuantity => checked((int)Stacks
        .Where(stack => stack.ScopeKind == RetainerBrowseScopeKind.Player)
        .Sum(stack => (long)stack.Quantity));
    public int RetainerQuantity => checked((int)Stacks
        .Where(stack => stack.ScopeKind == RetainerBrowseScopeKind.Retainer)
        .Sum(stack => (long)stack.Quantity));
    public bool CanWithdrawToPlayer => RetainerQuantity > 0;
    public IReadOnlyCollection<FfxivRetainerKey> Retainers => Stacks
        .Where(stack => stack.RetainerId is not null)
        .Select(stack => new FfxivRetainerKey(stack.RetainerId!.Value))
        .Distinct()
        .ToArray();
}

/// <summary>
/// One observed market listing belonging to an owner-scoped retainer. Price and condition remain evidence rather than
/// collapsing an unknown observation into a zero value.
/// </summary>
public sealed record RetainerBrowseMarketListing(
    string ScopeKey,
    ulong RetainerId,
    string RetainerName,
    uint ItemId,
    string ItemName,
    string? ItemType,
    int Quantity,
    FfxivItemQuality Quality,
    FieldEvidence<decimal> Condition,
    FieldEvidence<decimal> UnitPrice,
    FieldEvidence<decimal> TotalPrice)
{
    public FfxivOfferSource Source => FfxivOfferSource.Market;
    public FfxivRetainerKey Retainer => new(RetainerId);
}

/// <summary>Deterministic content and vocabulary identities for query/cache invalidation.</summary>
public sealed record RetainerBrowseProjectionIdentity(string Data, string Context);

/// <summary>
/// Pure current-character browser projection. It carries no cache freshness, observation age, or other user-facing
/// time-derived evidence.
/// </summary>
public sealed class RetainerBrowseProjection
{
    private readonly IReadOnlyDictionary<string, RetainerBrowseScopeOption> scopesByKey;
    private readonly Dictionary<string, IReadOnlyList<RetainerBrowseItemGroup>> itemGroupsByScope = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RetainerBrowseMarketListing>> listingsByScope = new(StringComparer.Ordinal);

    internal RetainerBrowseProjection(
        RetainerBrowseProjectionIdentity identity,
        IReadOnlyList<RetainerBrowseScopeOption> scopes,
        IReadOnlyList<RetainerBrowseItemGroup> itemGroups,
        IReadOnlyList<RetainerBrowseMarketListing> listings)
    {
        Identity = identity;
        Scopes = scopes;
        ItemGroups = itemGroups;
        Listings = listings;
        scopesByKey = scopes.ToDictionary(scope => scope.Key, StringComparer.Ordinal);
        itemGroupsByScope[RetainerBrowseScopeOption.AllKey] = itemGroups;
        listingsByScope[RetainerBrowseScopeOption.AllKey] = listings;
    }

    public RetainerBrowseProjectionIdentity Identity { get; }
    public IReadOnlyList<RetainerBrowseScopeOption> Scopes { get; }
    public IReadOnlyList<RetainerBrowseItemGroup> ItemGroups { get; }
    public IReadOnlyList<RetainerBrowseMarketListing> Listings { get; }

    public IReadOnlyList<RetainerBrowseItemGroup> GetItemGroups(string? scopeKey = null)
    {
        var scope = GetScope(scopeKey);
        if (itemGroupsByScope.TryGetValue(scope.Key, out var cached))
            return cached;

        var scoped = RetainerBrowseProjectionBuilder.AggregateItems(ItemGroups
            .SelectMany(group => group.Stacks)
            .Where(stack => string.Equals(stack.ScopeKey, scope.Key, StringComparison.Ordinal)));
        itemGroupsByScope[scope.Key] = scoped;
        return scoped;
    }

    public IReadOnlyList<RetainerBrowseMarketListing> GetListings(string? scopeKey = null)
    {
        var scope = GetScope(scopeKey);
        if (listingsByScope.TryGetValue(scope.Key, out var cached))
            return cached;

        var scoped = Listings
            .Where(listing => string.Equals(listing.ScopeKey, scope.Key, StringComparison.Ordinal))
            .ToArray();
        listingsByScope[scope.Key] = scoped;
        return scoped;
    }

    private RetainerBrowseScopeOption GetScope(string? scopeKey)
    {
        var key = string.IsNullOrWhiteSpace(scopeKey) ? RetainerBrowseScopeOption.AllKey : scopeKey.Trim();
        return scopesByKey.TryGetValue(key, out var scope)
            ? scope
            : throw new ArgumentOutOfRangeException(nameof(scopeKey), key, "The browser scope is not available in this projection.");
    }
}

public static class RetainerBrowseProjectionBuilder
{
    public static RetainerBrowseProjection Build(
        IReadOnlyList<InventoryBag> playerBags,
        Configuration config,
        RetainerOwnerScope? ownerScope)
    {
        ArgumentNullException.ThrowIfNull(playerBags);
        ArgumentNullException.ThrowIfNull(config);

        var scopes = new List<RetainerBrowseScopeOption>
        {
            RetainerBrowseScopeOption.All,
            RetainerBrowseScopeOption.Player,
        };
        var stacks = new List<RetainerBrowseStockStack>();
        var listings = new List<RetainerBrowseMarketListing>();

        foreach (var bag in playerBags)
        foreach (var item in bag.Items)
        {
            if (!IsPositive(item.ItemId, item.Quantity))
                continue;

            stacks.Add(new RetainerBrowseStockStack(
                RetainerBrowseScopeOption.PlayerKey,
                RetainerBrowseScopeKind.Player,
                null,
                "Player",
                DisplayStorage(item.ContainerKey, bag.Location, bag.BagName),
                item.SlotIndex,
                item.ItemId,
                DisplayName(item.ItemId, item.ItemName),
                item.ItemType,
                checked((int)item.Quantity),
                item.IsHQ ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
                ResolveCondition(item.ConditionPercent, item.Condition)));
        }

        // A missing current-character identity never widens access to every cached retainer.
        var scopedRetainers = ownerScope is { IsAvailable: true }
            ? config.RetainerCache.Values
                .Where(retainer => retainer.RetainerId != 0 && ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld))
                .OrderBy(retainer => retainer.RetainerId)
                .ThenBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        foreach (var retainer in scopedRetainers)
        {
            var retainerName = DisplayRetainerName(retainer);
            var scopeKey = RetainerBrowseScopeOption.RetainerKey(retainer.RetainerId);
            scopes.Add(new RetainerBrowseScopeOption(scopeKey, retainerName, RetainerBrowseScopeKind.Retainer, retainer.RetainerId));

            foreach (var bag in retainer.Bags.Where(bag => IsInventoryBag(bag.BagName)))
            foreach (var item in bag.Items)
            {
                if (!IsPositive(item.ItemId, item.Quantity))
                    continue;

                stacks.Add(new RetainerBrowseStockStack(
                    scopeKey,
                    RetainerBrowseScopeKind.Retainer,
                    retainer.RetainerId,
                    retainerName,
                    DisplayStorage(item.ContainerKey, bag.Location, bag.BagName),
                    item.SlotIndex,
                    item.ItemId,
                    DisplayName(item.ItemId, item.ItemName),
                    item.ItemType,
                    checked((int)item.Quantity),
                    item.IsHQ ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
                    ResolveCondition(item.ConditionPercent, item.Condition)));
            }

            foreach (var listing in retainer.MarketListings)
            {
                if (!IsPositive(listing.ItemId, listing.Quantity))
                    continue;

                var price = listing.UnitPrice is { } unitPrice
                    ? Evidence.Known((decimal)unitPrice)
                    : Evidence.Unknown<decimal>("The listing unit price was not recorded.");
                listings.Add(new RetainerBrowseMarketListing(
                    scopeKey,
                    retainer.RetainerId,
                    retainerName,
                    listing.ItemId,
                    DisplayName(listing.ItemId, listing.ItemName),
                    listing.ItemType,
                    checked((int)listing.Quantity),
                    listing.IsHQ ? FfxivItemQuality.HQ : FfxivItemQuality.NQ,
                    ResolveCondition(listing.ConditionPercent, listing.Condition),
                    price,
                    price.IsKnown
                        ? Evidence.Known(price.Value * listing.Quantity)
                        : Evidence.Unknown<decimal>("The listing total cannot be calculated without a unit price.")));
            }
        }

        var itemGroups = AggregateItems(stacks);
        var sortedListings = listings
            .OrderBy(listing => listing.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.ItemId)
            .ThenBy(listing => listing.RetainerId)
            .ToArray();
        var orderedScopes = scopes
            .Take(2)
            .Concat(scopes.Skip(2)
                .OrderBy(scope => scope.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(scope => scope.RetainerId))
            .ToArray();

        return new RetainerBrowseProjection(
            new RetainerBrowseProjectionIdentity(
                RetainerBrowseIdentity.CreateData(itemGroups, sortedListings),
                RetainerBrowseIdentity.CreateContext(itemGroups, sortedListings, orderedScopes, RetainerBrowseScopeOption.AllKey)),
            orderedScopes,
            itemGroups,
            sortedListings);
    }

    internal static IReadOnlyList<RetainerBrowseItemGroup> AggregateItems(IEnumerable<RetainerBrowseStockStack> stacks) => stacks
        .GroupBy(stack => stack.ItemId)
        .Select(group =>
        {
            var orderedStacks = group
                .OrderBy(stack => stack.ScopeKind)
                .ThenBy(stack => stack.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(stack => stack.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(stack => stack.Quantity)
                .ToArray();
            var itemName = orderedStacks
                .Select(stack => stack.ItemName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {group.Key.ToString(CultureInfo.InvariantCulture)}";
            var itemType = orderedStacks.Select(stack => stack.ItemType).FirstOrDefault(type => !string.IsNullOrWhiteSpace(type));
            return new RetainerBrowseItemGroup(group.Key, itemName, itemType, orderedStacks);
        })
        .OrderBy(group => group.ItemName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.ItemId)
        .ToArray();

    private static bool IsPositive(uint itemId, uint quantity) => itemId != 0 && quantity != 0;

    private static bool IsInventoryBag(string bagName) =>
        !bagName.Equals("RetainerGil", StringComparison.OrdinalIgnoreCase) &&
        !bagName.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(uint itemId, string? itemName) =>
        string.IsNullOrWhiteSpace(itemName) ? $"Item {itemId.ToString(CultureInfo.InvariantCulture)}" : itemName;

    private static string DisplayRetainerName(CachedRetainer retainer) =>
        string.IsNullOrWhiteSpace(retainer.RetainerName)
            ? $"Retainer {retainer.RetainerId.ToString(CultureInfo.InvariantCulture)}"
            : retainer.RetainerName;

    private static string DisplayStorage(string? containerKey, string? location, string bagName) =>
        !string.IsNullOrWhiteSpace(containerKey)
            ? containerKey
            : !string.IsNullOrWhiteSpace(location) ? location : bagName;

    private static FieldEvidence<decimal> ResolveCondition(float? conditionPercent, float legacyCondition)
    {
        if (conditionPercent is >= 0 and <= 100)
            return Evidence.Known((decimal)conditionPercent.Value);
        if (conditionPercent is not null)
            return Evidence.Unknown<decimal>("The recorded condition percentage is outside the supported range.");
        if (legacyCondition <= 0)
            return Evidence.Unknown<decimal>("The legacy condition value cannot distinguish zero from missing evidence.");

        var normalized = legacyCondition <= 1 ? legacyCondition * 100 : legacyCondition;
        return normalized <= 100
            ? Evidence.Known((decimal)normalized)
            : Evidence.Unknown<decimal>("The legacy condition value is outside the supported range.");
    }
}

/// <summary>UI-neutral, explicit staging/upsert seam for the existing withdrawal-plan persistence model.</summary>
public static class RetainerBrowseWithdrawalPlanStager
{
    public static bool TryUpsert(
        IList<RetainerRestockPlanItem> planItems,
        RetainerBrowseItemGroup itemGroup,
        int desiredPlayerQuantity)
    {
        ArgumentNullException.ThrowIfNull(planItems);
        ArgumentNullException.ThrowIfNull(itemGroup);
        if (!itemGroup.CanWithdrawToPlayer || desiredPlayerQuantity <= 0)
            return false;

        var existing = planItems.FirstOrDefault(item => item.ItemId == itemGroup.ItemId);
        if (existing is null)
        {
            planItems.Add(new RetainerRestockPlanItem
            {
                ItemId = itemGroup.ItemId,
                ItemName = itemGroup.ItemName,
                DesiredPlayerQuantity = desiredPlayerQuantity,
                Enabled = true,
            });
        }
        else
        {
            existing.ItemName = itemGroup.ItemName;
            existing.DesiredPlayerQuantity = desiredPlayerQuantity;
            existing.Enabled = true;
        }

        return true;
    }
}

public enum RetainerBrowseQueryMode
{
    Items,
    Listings,
}

public sealed record RetainerBrowseFilterStatus(
    bool IsValid,
    bool IsShowingLastValidResults,
    string NormalizedExpression,
    string SemanticExpression,
    IReadOnlyList<FilterDiagnostic> Diagnostics);

public sealed record RetainerBrowseItemsQueryResult(
    IReadOnlyList<RetainerBrowseItemGroup> Items,
    RetainerBrowseFilterStatus Filter);

public sealed record RetainerBrowseListingsQueryResult(
    IReadOnlyList<RetainerBrowseMarketListing> Listings,
    RetainerBrowseFilterStatus Filter);

/// <summary>
/// Stateful filter controller for the native browser. It recompiles only when its expression or named-value context
/// changes, and an invalid edit continues presenting the last valid result set.
/// </summary>
public sealed class RetainerBrowseQueryController
{
    private readonly QueryState<RetainerBrowseItemGroup> items = new();
    private readonly QueryState<RetainerBrowseMarketListing> listings = new();

    public int ItemCompilationCount => items.CompilationCount;
    public int ListingCompilationCount => listings.CompilationCount;
    public int ItemEvaluationCount => items.EvaluationCount;
    public int ListingEvaluationCount => listings.EvaluationCount;

    public RetainerBrowseItemsQueryResult QueryItems(
        RetainerBrowseProjection projection,
        string? expression,
        string? scopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var source = projection.GetItemGroups(scopeKey);
        var context = items.EnsureContext(
            RetainerBrowseIdentity.CreateContext(source, [], projection.Scopes, scopeKey),
            () => RetainerBrowseFilterContexts.CreateItems(source));
        var query = items.Query(source, RetainerBrowseIdentity.CreateData(source, []), expression, context);
        return new RetainerBrowseItemsQueryResult(query.Rows, query.Filter);
    }

    public RetainerBrowseListingsQueryResult QueryListings(
        RetainerBrowseProjection projection,
        string? expression,
        string? scopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var source = projection.GetListings(scopeKey);
        var context = listings.EnsureContext(
            RetainerBrowseIdentity.CreateContext([], source, projection.Scopes, scopeKey),
            () => RetainerBrowseFilterContexts.CreateListings(source));
        var query = listings.Query(source, RetainerBrowseIdentity.CreateData([], source), expression, context);
        return new RetainerBrowseListingsQueryResult(query.Rows, query.Filter);
    }

    public FilterContext<RetainerBrowseItemGroup> GetItemsContext(
        RetainerBrowseProjection projection,
        string? scopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var source = projection.GetItemGroups(scopeKey);
        return items.EnsureContext(
            RetainerBrowseIdentity.CreateContext(source, [], projection.Scopes, scopeKey),
            () => RetainerBrowseFilterContexts.CreateItems(source));
    }

    public FilterContext<RetainerBrowseMarketListing> GetListingsContext(
        RetainerBrowseProjection projection,
        string? scopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var source = projection.GetListings(scopeKey);
        return listings.EnsureContext(
            RetainerBrowseIdentity.CreateContext([], source, projection.Scopes, scopeKey),
            () => RetainerBrowseFilterContexts.CreateListings(source));
    }

    public FilterReferenceModel GetReference(
        RetainerBrowseProjection projection,
        RetainerBrowseQueryMode mode,
        string? scopeKey = null) =>
        mode == RetainerBrowseQueryMode.Items
            ? FilterReferenceGenerator.Create(GetItemsContext(projection, scopeKey))
            : FilterReferenceGenerator.Create(GetListingsContext(projection, scopeKey));

    public FilterCompletionResult Complete(
        RetainerBrowseProjection projection,
        RetainerBrowseQueryMode mode,
        string? expression,
        int? caretPosition = null,
        string? scopeKey = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var value = expression ?? string.Empty;
        var caret = Math.Clamp(caretPosition ?? value.Length, 0, value.Length);
        return mode == RetainerBrowseQueryMode.Items
            ? FilterCompletionService.Complete(items.EnsureContext(
                RetainerBrowseIdentity.CreateContext(projection.GetItemGroups(scopeKey), [], projection.Scopes, scopeKey),
                () => RetainerBrowseFilterContexts.CreateItems(projection.GetItemGroups(scopeKey))),
                new FilterCompletionRequest("retainer-browse-items", value, caret))
            : FilterCompletionService.Complete(listings.EnsureContext(
                RetainerBrowseIdentity.CreateContext([], projection.GetListings(scopeKey), projection.Scopes, scopeKey),
                () => RetainerBrowseFilterContexts.CreateListings(projection.GetListings(scopeKey))),
                new FilterCompletionRequest("retainer-browse-listings", value, caret));
    }

    private sealed class QueryState<TRecord>
    {
        private FilterContext<TRecord>? context;
        private string? contextIdentity;
        private FilterCompilation<TRecord>? currentCompilation;
        private string currentExpression = string.Empty;
        private FilterCompilation<TRecord>? lastValidCompilation;
        private IReadOnlyList<TRecord> lastValidRows = [];
        private string? lastValidDataIdentity;
        private bool hasLastValidResults;

        public int CompilationCount { get; private set; }
        public int EvaluationCount { get; private set; }

        public FilterContext<TRecord> EnsureContext(string identity, Func<FilterContext<TRecord>> create)
        {
            if (context is null || !string.Equals(identity, contextIdentity, StringComparison.Ordinal))
            {
                context = create();
                contextIdentity = identity;
                currentCompilation = null;
                lastValidCompilation = null;
                lastValidRows = [];
                lastValidDataIdentity = null;
                hasLastValidResults = false;
            }

            return context;
        }

        public (IReadOnlyList<TRecord> Rows, RetainerBrowseFilterStatus Filter) Query(
            IReadOnlyList<TRecord> source,
            string dataIdentity,
            string? expression,
            FilterContext<TRecord> currentContext)
        {
            var value = expression ?? string.Empty;
            if (currentCompilation is null || !string.Equals(value, currentExpression, StringComparison.Ordinal))
            {
                currentExpression = value;
                currentCompilation = FilterCompiler.Compile(value, currentContext);
                CompilationCount++;
            }

            if (currentCompilation.IsValid)
            {
                if (!ReferenceEquals(currentCompilation, lastValidCompilation) ||
                    !string.Equals(dataIdentity, lastValidDataIdentity, StringComparison.Ordinal))
                {
                    lastValidRows = source.Where(currentCompilation.Matches).ToArray();
                    lastValidDataIdentity = dataIdentity;
                    EvaluationCount++;
                }

                lastValidCompilation = currentCompilation;
                hasLastValidResults = true;
                return (lastValidRows, ToStatus(currentCompilation, false));
            }

            return (lastValidRows, ToStatus(currentCompilation, hasLastValidResults));
        }

        private static RetainerBrowseFilterStatus ToStatus(FilterCompilation<TRecord> compilation, bool isShowingLastValidResults) =>
            new(
                compilation.IsValid,
                isShowingLastValidResults,
                compilation.NormalizedExpression,
                compilation.SemanticExpression,
                compilation.Diagnostics);
    }
}

internal static class RetainerBrowseFilterContexts
{
    public static FilterContext<RetainerBrowseItemGroup> CreateItems(IReadOnlyList<RetainerBrowseItemGroup> source)
    {
        var vocabulary = CreateVocabulary(source.Select(group => (group.ItemId, group.ItemName)), source.SelectMany(group => group.Stacks));
        return new FilterContextBuilder<RetainerBrowseItemGroup>(vocabulary.Catalog)
            .Bind(vocabulary.ItemName, group => Evidence.Known(new FfxivItemKey(group.ItemId)))
            .Bind(vocabulary.OwnershipOwned, group => Evidence.Known(group.TotalQuantity > 0))
            .Bind(vocabulary.OwnershipQuantity, group => Evidence.Known((long)group.TotalQuantity))
            .BindSet(vocabulary.OwnershipRetainers, group => Evidence.Known(group.Retainers))
            .UseDefaultText(vocabulary.ItemName, group => Evidence.Known(group.ItemName))
            .Build("retainer-browse-items", "1");
    }

    public static FilterContext<RetainerBrowseMarketListing> CreateListings(IReadOnlyList<RetainerBrowseMarketListing> source)
    {
        var vocabulary = CreateVocabulary(source.Select(listing => (listing.ItemId, listing.ItemName)), source);
        return new FilterContextBuilder<RetainerBrowseMarketListing>(vocabulary.Catalog)
            .Bind(vocabulary.ItemName, listing => Evidence.Known(new FfxivItemKey(listing.ItemId)))
            .Bind(vocabulary.InstanceQuality, listing => Evidence.Known(listing.Quality))
            .Bind(vocabulary.InstanceCondition, listing => listing.Condition)
            .Bind(vocabulary.OfferSource, listing => Evidence.Known(listing.Source))
            .Bind(vocabulary.OfferPrice, listing => listing.UnitPrice)
            .Bind(vocabulary.OfferTotalPrice, listing => listing.TotalPrice)
            .Bind(vocabulary.OfferQuantity, listing => Evidence.Known((long)listing.Quantity))
            // offer.age intentionally has no binding: cache/listing observation age is unavailable in this browser.
            .BindSet(vocabulary.OwnershipRetainers, listing => Evidence.Known<IReadOnlyCollection<FfxivRetainerKey>>([listing.Retainer]))
            .UseDefaultText(vocabulary.ItemName, listing => Evidence.Known(listing.ItemName))
            .Build("retainer-browse-listings", "1");
    }

    private static FfxivFilterCatalog CreateVocabulary(
        IEnumerable<(uint ItemId, string ItemName)> itemCandidates,
        IEnumerable<RetainerBrowseStockStack> stacks) =>
        CreateVocabulary(itemCandidates, stacks
            .Where(stack => stack.RetainerId is not null)
            .Select(stack => (stack.RetainerId!.Value, stack.OwnerName)));

    private static FfxivFilterCatalog CreateVocabulary(
        IEnumerable<(uint ItemId, string ItemName)> itemCandidates,
        IEnumerable<RetainerBrowseMarketListing> listings) =>
        CreateVocabulary(itemCandidates, listings.Select(listing => (listing.RetainerId, listing.RetainerName)));

    private static FfxivFilterCatalog CreateVocabulary(
        IEnumerable<(uint ItemId, string ItemName)> itemCandidates,
        IEnumerable<(ulong RetainerId, string RetainerName)> retainerCandidates)
    {
        var items = itemCandidates
            .GroupBy(candidate => candidate.ItemId)
            .OrderBy(group => group.Key)
            .Select(group => new FilterLiteralCandidate<FfxivItemKey>(new(group.Key), group.First().ItemName))
            .ToArray();
        var retainers = retainerCandidates
            .GroupBy(candidate => candidate.RetainerId)
            .OrderBy(group => group.Key)
            .Select(group => new FilterLiteralCandidate<FfxivRetainerKey>(new(group.Key), group.First().RetainerName))
            .ToArray();
        return FfxivFilterCatalog.Create(new FfxivFilterResolvers(
            new FilterNamedValueCatalog<FfxivItemKey>(items),
            EmptyResolver<FfxivJobKey>(),
            EmptyResolver<FfxivUiCategoryKey>(),
            EmptyResolver<FfxivCharacterKey>(),
            new FilterNamedValueCatalog<FfxivRetainerKey>(retainers),
            EmptyResolver<FfxivWorldKey>(),
            EmptyResolver<FfxivDataCenterKey>()));
    }

    private static FilterNamedValueCatalog<T> EmptyResolver<T>() => new([]);
}

internal static class RetainerBrowseIdentity
{
    public static string CreateData(
        IEnumerable<RetainerBrowseItemGroup> items,
        IEnumerable<RetainerBrowseMarketListing> listings) =>
        Hash(items
            .OrderBy(item => item.ItemId)
            .SelectMany(item => item.Stacks.Select(stack => $"S|{stack.ScopeKey}|{stack.StorageName}|{stack.SlotIndex}|{stack.ItemId}|{stack.ItemName}|{stack.ItemType}|{stack.Quantity}|{stack.Quality}|{EvidenceKey(stack.Condition)}"))
            .Concat(listings.OrderBy(listing => listing.RetainerId).ThenBy(listing => listing.ItemId).Select(listing =>
                $"L|{listing.ScopeKey}|{listing.RetainerId}|{listing.ItemId}|{listing.ItemName}|{listing.ItemType}|{listing.Quantity}|{listing.Quality}|{EvidenceKey(listing.Condition)}|{EvidenceKey(listing.UnitPrice)}|{EvidenceKey(listing.TotalPrice)}")));

    public static string CreateContext(
        IEnumerable<RetainerBrowseItemGroup> items,
        IEnumerable<RetainerBrowseMarketListing> listings,
        IEnumerable<RetainerBrowseScopeOption> scopes,
        string? selectedScope) =>
        Hash(new[] { $"scope|{selectedScope ?? RetainerBrowseScopeOption.AllKey}" }
            .Concat(items.OrderBy(item => item.ItemId).Select(item => $"I|{item.ItemId}|{item.ItemName}"))
            .Concat(listings.OrderBy(listing => listing.ItemId).ThenBy(listing => listing.RetainerId).Select(listing => $"I|{listing.ItemId}|{listing.ItemName}|R|{listing.RetainerId}|{listing.RetainerName}"))
            .Concat(items.SelectMany(item => item.Stacks).Where(stack => stack.RetainerId is not null)
                .OrderBy(stack => stack.RetainerId).Select(stack => $"R|{stack.RetainerId}|{stack.OwnerName}"))
            .Concat(scopes.OrderBy(scope => scope.Key).Select(scope => $"P|{scope.Key}|{scope.DisplayName}")));

    private static string EvidenceKey(FieldEvidence<decimal> evidence) => evidence.IsKnown
        ? $"K:{evidence.Value.ToString(CultureInfo.InvariantCulture)}"
        : $"U:{evidence.UnknownReason}";

    private static string Hash(IEnumerable<string> values)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var value in values)
        {
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
            hash ^= '|';
            hash *= prime;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }
}
