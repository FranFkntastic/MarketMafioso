namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class StockAvailabilityServiceTests
{
    [Fact]
    public void Analyze_TargetQuantityLineReportsEnoughWhenEligibleDepthMeetsTarget()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 5, maxUnitPrice: 100),
            [
                CreateListing("cheap-1", quantity: 2, unitPrice: 90),
                CreateListing("cheap-2", quantity: 3, unitPrice: 100),
                CreateListing("too-expensive", quantity: 99, unitPrice: 101),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Enough, result.Status);
        Assert.Equal(5u, result.EligibleQuantity);
        Assert.Equal(2, result.EligibleListingCount);
        Assert.Equal(5u, result.RequiredQuantity);
        Assert.False(result.IsOpenEndedDepth);
    }

    [Fact]
    public void Analyze_CappedAllBelowThresholdLineReportsPartialAgainstPurchaseCap()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "AllBelowThreshold", purchaseCap: 10, maxUnitPrice: 100),
            [
                CreateListing("eligible", quantity: 4, unitPrice: 100),
                CreateListing("zero-price", quantity: 4, unitPrice: 0),
                CreateListing("wrong-item", quantity: 20, unitPrice: 50, itemId: 999),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Partial, result.Status);
        Assert.Equal(4u, result.EligibleQuantity);
        Assert.Equal(1, result.EligibleListingCount);
        Assert.Equal(10u, result.RequiredQuantity);
        Assert.Equal(6u, result.ShortfallQuantity);
    }

    [Fact]
    public void Analyze_CappedOrTargetLineReportsNoneWhenNoListingsMatchConstraints()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 5, hqPolicy: "HqOnly", maxUnitPrice: 100),
            [
                CreateListing("nq", quantity: 5, unitPrice: 90, hq: false),
                CreateListing("zero-quantity", quantity: 0, unitPrice: 90, hq: true),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.None, result.Status);
        Assert.Equal(0u, result.EligibleQuantity);
        Assert.Equal(0, result.EligibleListingCount);
        Assert.Equal(5u, result.ShortfallQuantity);
    }

    [Fact]
    public void Analyze_TargetQuantityLineWithoutPositiveTargetReportsInvalid()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 0, maxUnitPrice: 100),
            [CreateListing("eligible", quantity: 5, unitPrice: 90)]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Invalid, result.Status);
        Assert.Contains("target quantity", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_UncappedAllBelowThresholdReportsDepthNotSufficiency()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "AllBelowThreshold", purchaseCap: null, maxUnitPrice: 100),
            [
                CreateListing("nq", quantity: 2, unitPrice: 90, hq: false),
                CreateListing("hq", quantity: 7, unitPrice: 100, hq: true),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Depth, result.Status);
        Assert.True(result.IsOpenEndedDepth);
        Assert.Equal(9u, result.EligibleQuantity);
        Assert.Equal(2, result.EligibleListingCount);
        Assert.Null(result.RequiredQuantity);
        Assert.Null(result.ShortfallQuantity);
        Assert.DoesNotContain("enough", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("short", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_WhenEligibleQuantityExceedsUintMaxValue_ClampsDepthQuantity()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "AllBelowThreshold", purchaseCap: null, maxUnitPrice: 100),
            [
                CreateListing("max", quantity: uint.MaxValue, unitPrice: 90),
                CreateListing("overflow", quantity: 1, unitPrice: 90),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Depth, result.Status);
        Assert.Equal(uint.MaxValue, result.EligibleQuantity);
        Assert.Equal(2, result.EligibleListingCount);
    }

    [Fact]
    public void Analyze_FiltersByRouteScopeWorlds()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(
                quantityMode: "AllBelowThreshold",
                purchaseCap: null,
                maxUnitPrice: 100,
                routeWorlds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Siren" }),
            [
                CreateListing("in-scope", quantity: 2, unitPrice: 90, worldName: "Siren"),
                CreateListing("out-of-scope", quantity: 7, unitPrice: 90, worldName: "Faerie"),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Depth, result.Status);
        Assert.Equal(2u, result.EligibleQuantity);
        Assert.Equal(["in-scope"], result.EligibleListings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void Analyze_FiltersByRouteScopeWorldsCaseInsensitivelyRegardlessOfSetComparer()
    {
        var result = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(
                quantityMode: "AllBelowThreshold",
                purchaseCap: null,
                maxUnitPrice: 100,
                routeWorlds: new HashSet<string> { "siren" }),
            [
                CreateListing("case-match", quantity: 2, unitPrice: 90, worldName: "Siren"),
                CreateListing("out-of-scope", quantity: 7, unitPrice: 90, worldName: "Faerie"),
            ]);

        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Depth, result.Status);
        Assert.Equal(2u, result.EligibleQuantity);
        Assert.Equal(["case-match"], result.EligibleListings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void Analyze_ThresholdAndQuantityChangesReanalyzeCachedRowsWithoutReplacingSnapshot()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(5));
        var key = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis");
        cache.Replace(
            key,
            [
                CreateListing("cheap", quantity: 3, unitPrice: 90),
                CreateListing("mid", quantity: 4, unitPrice: 150),
            ],
            fetchedAtUtc: DateTimeOffset.UnixEpoch,
            sourceFreshness: "source",
            diagnosticStatus: "Fresh",
            diagnosticSummary: "Fetched once.");
        var snapshot = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddMinutes(1)).Snapshot
            ?? throw new InvalidOperationException("Expected cached snapshot.");

        var lowerThreshold = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 6, maxUnitPrice: 100),
            snapshot.Listings);
        var higherThresholdLowerQuantity = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 6, maxUnitPrice: 150),
            snapshot.Listings);
        var higherThresholdHigherQuantity = MarketMafioso.MarketAcquisition.StockAvailabilityService.Analyze(
            CreateRequest(quantityMode: "TargetQuantity", desiredQuantity: 8, maxUnitPrice: 150),
            snapshot.Listings);

        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.FetchedAtUtc);
        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Partial, lowerThreshold.Status);
        Assert.Equal(3u, lowerThreshold.EligibleQuantity);
        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Enough, higherThresholdLowerQuantity.Status);
        Assert.Equal(7u, higherThresholdLowerQuantity.EligibleQuantity);
        Assert.Equal(MarketMafioso.MarketAcquisition.StockAvailabilityStatus.Partial, higherThresholdHigherQuantity.Status);
        Assert.Equal(7u, higherThresholdHigherQuantity.EligibleQuantity);
        Assert.Equal(1, cache.Count);
    }

    private static MarketMafioso.MarketAcquisition.StockAvailabilityRequest CreateRequest(
        string quantityMode,
        uint maxUnitPrice,
        uint? desiredQuantity = null,
        uint? purchaseCap = null,
        string hqPolicy = "Either",
        IReadOnlySet<string>? routeWorlds = null) =>
        new(
            LineId: "line-1",
            ItemId: 2,
            QuantityMode: quantityMode,
            HqPolicy: hqPolicy,
            MaxUnitPrice: maxUnitPrice,
            DesiredQuantity: desiredQuantity,
            PurchaseCap: purchaseCap,
            RouteWorlds: routeWorlds);

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionListing CreateListing(
        string listingId,
        uint quantity,
        uint unitPrice,
        uint itemId = 2,
        bool hq = false,
        string worldName = "Siren") =>
        new()
        {
            ItemId = itemId,
            ListingId = listingId,
            WorldName = worldName,
            WorldId = 57,
            RetainerName = "Retainer",
            RetainerId = "retainer",
            Quantity = quantity,
            UnitPrice = unitPrice,
            IsHq = hq,
            LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
        };
}
