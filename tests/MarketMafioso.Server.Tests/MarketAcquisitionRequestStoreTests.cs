namespace MarketMafioso.Server.Tests;

public sealed class MarketAcquisitionRequestStoreTests
{
    [Fact]
    public async Task CreateBatchAsyncAllowsConfiguredLongPickupExpiry()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync(
            new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMaximumExpirySeconds", "86400"));
        var request = CreateBatchRequest("long-pickup-expiry", expiresInSeconds: 7200);

        var created = await fixture.Store.CreateBatchAsync(request, CancellationToken.None);

        var lifetime = created.Request.ExpiresAtUtc - created.Request.CreatedAtUtc;
        Assert.True(lifetime >= TimeSpan.FromMinutes(119), $"Expected roughly two hours, got {lifetime}.");
    }

    [Fact]
    public async Task CreateBatchAsyncReturnsQuickShopOriginMetadata()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var request = CreateBatchRequest("quick-shop-origin") with
        {
            Origin = MarketAcquisitionOrigins.ClientQuickShop,
            CreatedByPluginInstanceId = "plugin-instance-1",
        };

        var created = await fixture.Store.CreateBatchAsync(request, CancellationToken.None);
        var claimed = await fixture.Store.ClaimAsync(
            created.Request.Id,
            new MarketAcquisitionClaimRequest
            {
                CharacterName = MarketAcquisitionTestApp.CharacterName,
                World = MarketAcquisitionTestApp.WorldName,
                PluginInstanceId = "plugin-instance-1",
            },
            CancellationToken.None);

        Assert.Equal(MarketAcquisitionOrigins.ClientQuickShop, created.Request.Origin);
        Assert.Equal("plugin-instance-1", created.Request.CreatedByPluginInstanceId);
        Assert.NotNull(claimed);
        Assert.Equal(MarketAcquisitionOrigins.ClientQuickShop, claimed.Origin);
        Assert.Equal("plugin-instance-1", claimed.CreatedByPluginInstanceId);
    }

    [Fact]
    public async Task CreateBatchAsyncDefaultsMissingOriginToDashboardCreated()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var request = CreateBatchRequest("dashboard-origin-default") with
        {
            Origin = string.Empty,
        };

        var created = await fixture.Store.CreateBatchAsync(request, CancellationToken.None);

        Assert.Equal(MarketAcquisitionOrigins.DashboardCreated, created.Request.Origin);
        Assert.Null(created.Request.CreatedByPluginInstanceId);
    }

    [Fact]
    public async Task CreateBatchAsyncRejectsUnsupportedOrigin()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var request = CreateBatchRequest("bad-origin") with
        {
            Origin = "MysteryPanel",
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.CreateBatchAsync(request, CancellationToken.None));

        Assert.Contains("MysteryPanel", ex.Message, StringComparison.Ordinal);
        Assert.Contains("origin", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendLinesAsyncAddsDistinctPendingLineAndIncrementsRevision()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync(
            new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMaximumExpirySeconds", "86400"));
        var created = await fixture.Store.CreateBatchAsync(
            CreateBatchRequest("append-distinct-line", expiresInSeconds: 300),
            CancellationToken.None);

        var appended = await fixture.Store.AppendLinesAsync(
            created.Request.Id,
            new MarketAcquisitionBatchAppendLinesRequest
            {
                ExpectedRevision = created.Request.Revision,
                ExpiresInSeconds = 3600,
                Lines =
                [
                    CreateLine(4, "Lightning Shard", "Crystal", maxUnitPrice: 25),
                ],
            },
            CancellationToken.None);

        Assert.NotNull(appended);
        Assert.Equal(created.Request.Revision + 1, appended.Revision);
        Assert.Equal(2, appended.Lines.Count);
        Assert.Contains(appended.Lines, line => line.ItemId == 4 && line.Ordinal == 1);
        Assert.True(appended.ExpiresAtUtc - appended.CreatedAtUtc >= TimeSpan.FromMinutes(59));
    }

    [Fact]
    public async Task AppendLinesAsyncCoalescesMatchingPendingAllBelowThresholdLine()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateBatchAsync(
            CreateBatchRequest("append-coalesce-line"),
            CancellationToken.None);

        var appended = await fixture.Store.AppendLinesAsync(
            created.Request.Id,
            new MarketAcquisitionBatchAppendLinesRequest
            {
                ExpectedRevision = created.Request.Revision,
                ExpiresInSeconds = 300,
                Lines =
                [
                    CreateLine(2, "Fire Shard", "Crystal", maxQuantity: 250, maxUnitPrice: 99),
                ],
            },
            CancellationToken.None);

        Assert.NotNull(appended);
        Assert.Single(appended.Lines);
        Assert.Equal((uint)750, appended.Lines[0].MaxQuantity);
    }

    [Fact]
    public async Task AppendLinesAsyncRejectsClaimedBatch()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateClaimedBatchAsync("append-reject-claimed");
        var claimedView = await fixture.Store.GetAsync(claimed.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("Claimed test batch disappeared.");

        var ex = await Assert.ThrowsAsync<MarketAcquisitionInvalidTransitionException>(() =>
            fixture.Store.AppendLinesAsync(
                claimed.Id,
                new MarketAcquisitionBatchAppendLinesRequest
                {
                    ExpectedRevision = claimedView.Revision,
                    ExpiresInSeconds = 300,
                    Lines =
                    [
                        CreateLine(4, "Lightning Shard", "Crystal", maxUnitPrice: 25),
                    ],
                },
                CancellationToken.None));

        Assert.Contains(MarketAcquisitionStatuses.Claimed, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBatchAsyncRejectsSweepDataCenterOutsideRegion()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var request = new MarketAcquisitionBatchCreateRequest
        {
            SchemaVersion = 1,
            IdempotencyKey = "wrong-region-dc",
            TargetCharacterName = MarketAcquisitionTestApp.CharacterName,
            TargetWorld = MarketAcquisitionTestApp.WorldName,
            Region = "Oceania",
            WorldMode = "AllWorldSweep",
            SweepScope = "DataCenters",
            SweepDataCenters = ["Dynamis"],
            ExpiresInSeconds = 90,
            Lines =
            [
                new MarketAcquisitionBatchLineCreateRequest
                {
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    ItemKind = "Crystal",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 10,
                    MaxQuantity = 10,
                    HqPolicy = "Either",
                    MaxUnitPrice = 99,
                    GilCap = 990,
                },
            ],
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.CreateBatchAsync(request, CancellationToken.None));

        Assert.Contains("Dynamis", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Oceania", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordLineProgressAsyncRejectsLineFromDifferentBatch()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var first = await fixture.CreateAcceptedBatchAsync("wrong-batch-first", lineCount: 1);
        var second = await fixture.CreateAcceptedBatchAsync("wrong-batch-second", lineCount: 1);

        await Assert.ThrowsAsync<MarketAcquisitionInvalidLineException>(() =>
            fixture.Store.RecordLineProgressAsync(
                first.Id,
                second.Lines[0].LineId,
                new MarketAcquisitionLineProgressRequest
                {
                    ClaimToken = first.ClaimToken,
                    IdempotencyKey = "wrong-batch-line-key",
                    AttemptId = "attempt-1",
                    Sequence = 1,
                    Status = "Running",
                    Message = "Wrong line."
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RecordPurchaseAuditAsyncInsertsIdempotentPurchaseRecord()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateAcceptedBatchAsync("purchase-audit-idempotent", lineCount: 1);
        var request = new MarketAcquisitionPurchaseAuditRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = "purchase-audit-key",
            AttemptId = "attempt-1",
            Sequence = 1,
            LineId = claimed.Lines[0].LineId,
            WorldName = "Siren",
            ItemId = claimed.Lines[0].ItemId,
            ItemName = claimed.Lines[0].ItemName,
            ListingId = "listing-1",
            RetainerName = "Seller",
            RetainerId = "retainer-1",
            Quantity = 10,
            UnitPrice = 50,
            TotalGil = 500,
            IsHq = false,
            Result = "Purchased",
            Message = "Purchase confirmed."
        };

        var first = await fixture.Store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);
        var second = await fixture.Store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.AuditId, second.AuditId);
    }

    private static MarketAcquisitionBatchCreateRequest CreateBatchRequest(
        string idempotencyKey,
        int expiresInSeconds = 300) =>
        new()
        {
            SchemaVersion = 1,
            IdempotencyKey = idempotencyKey,
            TargetCharacterName = MarketAcquisitionTestApp.CharacterName,
            TargetWorld = MarketAcquisitionTestApp.WorldName,
            Region = "North America",
            WorldMode = "Recommended",
            SweepScope = "Region",
            ExpiresInSeconds = expiresInSeconds,
            Lines =
            [
                CreateLine(2, "Fire Shard", "Crystal", maxQuantity: 500, maxUnitPrice: 99),
            ],
        };

    private static MarketAcquisitionBatchLineCreateRequest CreateLine(
        uint itemId,
        string itemName,
        string itemKind,
        uint maxUnitPrice,
        uint maxQuantity = 0) =>
        new()
        {
            ItemId = itemId,
            ItemName = itemName,
            ItemKind = itemKind,
            QuantityMode = "AllBelowThreshold",
            TargetQuantity = 0,
            MaxQuantity = maxQuantity,
            HqPolicy = "Either",
            MaxUnitPrice = maxUnitPrice,
            GilCap = 0,
        };
}
