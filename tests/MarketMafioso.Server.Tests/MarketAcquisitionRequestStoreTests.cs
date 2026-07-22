using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Tests;

public sealed class MarketAcquisitionRequestStoreTests
{
    [Fact]
    public async Task CreateBatchAsyncUsesConfiguredPersistentDatabase()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();

        await fixture.Store.CreateBatchAsync(
            CreateBatchRequest("configured-database"),
            CancellationToken.None);

        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.ReleaseLocalDatabasePath));
    }

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
    public async Task CreateBatchAsyncReplaysSameBodyAndRejectsDifferentBody()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var request = CreateBatchRequest("create-idempotency");

        var created = await fixture.Store.CreateBatchAsync(request, CancellationToken.None);
        var replay = await fixture.Store.CreateBatchAsync(request, CancellationToken.None);
        var changed = request with
        {
            Lines = [CreateLine(4, "Lightning Shard", "Crystal", maxUnitPrice: 25)],
        };

        Assert.False(created.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(created.Request.Id, replay.Request.Id);
        await Assert.ThrowsAsync<MarketAcquisitionIdempotencyConflictException>(() =>
            fixture.Store.CreateBatchAsync(changed, CancellationToken.None));
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
    public async Task ReplaceBatchAsyncReplacesPendingBatchLinesAndIncrementsRevision()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync(
            new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMaximumExpirySeconds", "86400"));
        var created = await fixture.Store.CreateBatchAsync(
            CreateBatchRequest("replace-pending-lines", expiresInSeconds: 300),
            CancellationToken.None);

        var replaced = await fixture.Store.ReplaceBatchAsync(
            created.Request.Id,
            new MarketAcquisitionBatchReplaceRequest
            {
                ExpectedRevision = created.Request.Revision,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 3600,
                Lines =
                [
                    CreateLine(19951, "Koppranickel Ore", "Stone", maxQuantity: 25, maxUnitPrice: 276),
                ],
            },
            CancellationToken.None);

        Assert.NotNull(replaced);
        Assert.Equal(created.Request.Revision + 1, replaced.Revision);
        Assert.True(replaced.ExpiresAtUtc - replaced.CreatedAtUtc >= TimeSpan.FromMinutes(59));
        var line = Assert.Single(replaced.Lines);
        Assert.Equal((uint)19951, line.ItemId);
        Assert.Equal("Koppranickel Ore", line.ItemName);
        Assert.Equal((uint)25, line.MaxQuantity);
        Assert.Equal((uint)276, line.MaxUnitPrice);
    }

    [Fact]
    public async Task ReplaceBatchAsyncAllowsAcceptedUnstartedBatchAndPreservesStatus()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("replace-accepted-unstarted");
        var acceptedView = await fixture.Store.GetAsync(accepted.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("Accepted test batch disappeared.");

        var replaced = await fixture.Store.ReplaceBatchAsync(
            accepted.Id,
            new MarketAcquisitionBatchReplaceRequest
            {
                ExpectedRevision = acceptedView.Revision,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    CreateLine(19951, "Koppranickel Ore", "Stone", maxQuantity: 11, maxUnitPrice: 276),
                ],
            },
            CancellationToken.None);

        Assert.NotNull(replaced);
        Assert.Equal(MarketAcquisitionStatuses.AcceptedInPlugin, replaced.Status);
        Assert.Equal(acceptedView.Revision + 1, replaced.Revision);
        var line = Assert.Single(replaced.Lines);
        Assert.Equal((uint)19951, line.ItemId);
    }

    [Fact]
    public async Task ReplaceBatchAsyncLetsMostRecentReplacementSupersedeStaleRevision()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateBatchAsync(
            CreateBatchRequest("replace-stale-revision"),
            CancellationToken.None);
        var firstReplacement = await fixture.Store.ReplaceBatchAsync(
            created.Request.Id,
            new MarketAcquisitionBatchReplaceRequest
            {
                ExpectedRevision = created.Request.Revision,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    CreateLine(19951, "Koppranickel Ore", "Stone", maxQuantity: 10, maxUnitPrice: 276),
                ],
            },
            CancellationToken.None);
        Assert.NotNull(firstReplacement);

        var replaced = await fixture.Store.ReplaceBatchAsync(
            created.Request.Id,
            new MarketAcquisitionBatchReplaceRequest
            {
                ExpectedRevision = created.Request.Revision,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    CreateLine(19951, "Koppranickel Ore", "Stone", maxQuantity: 11, maxUnitPrice: 276),
                ],
            },
            CancellationToken.None);

        Assert.NotNull(replaced);
        Assert.Equal(created.Request.Revision + 2, replaced.Revision);
        Assert.Equal(11u, Assert.Single(replaced.Lines).MaxQuantity);
    }

    [Fact]
    public async Task ReplaceBatchAsyncRejectsBatchWithAttemptProgress()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("replace-progress-rejected");
        var acceptedView = await fixture.Store.GetAsync(accepted.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("Accepted test batch disappeared.");
        await fixture.Store.ReportProgressAsync(
            accepted.Id,
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = accepted.ClaimToken,
                IdempotencyKey = "replace-progress-rejected-progress",
                RunnerState = "Running",
                Message = "Route started.",
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<MarketAcquisitionInvalidTransitionException>(() =>
            fixture.Store.ReplaceBatchAsync(
                accepted.Id,
                new MarketAcquisitionBatchReplaceRequest
                {
                    ExpectedRevision = acceptedView.Revision,
                    Region = "North America",
                    WorldMode = "Recommended",
                    SweepScope = "Region",
                    ExpiresInSeconds = 300,
                    Lines =
                    [
                        CreateLine(19951, "Koppranickel Ore", "Stone", maxQuantity: 11, maxUnitPrice: 276),
                    ],
                },
                CancellationToken.None));
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
    public async Task LifecycleReplayIsIdempotentAndRejectsChangedBody()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateClaimedBatchAsync("lifecycle-idempotency");
        var request = new MarketAcquisitionLifecycleRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = "reject-once",
            Reason = "User rejected in plugin",
        };

        var rejected = await fixture.Store.RejectAsync(claimed.Id, request, CancellationToken.None);
        var replay = await fixture.Store.RejectAsync(claimed.Id, request, CancellationToken.None);

        Assert.Equal(MarketAcquisitionStatuses.Rejected, rejected!.Status);
        Assert.Equal(MarketAcquisitionStatuses.Rejected, replay!.Status);
        await Assert.ThrowsAsync<MarketAcquisitionIdempotencyConflictException>(() =>
            fixture.Store.RejectAsync(
                claimed.Id,
                request with { Reason = "Different body" },
                CancellationToken.None));
    }

    [Fact]
    public async Task AttemptProgressReplayEnforcesIdempotencyKeyAndSequence()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("attempt-idempotency");
        var request = CreateAttemptProgress(accepted.ClaimToken, "attempt-key-a", "Traveling.");

        var first = await fixture.Store.ReportAttemptProgressAsync(accepted.Id, request, CancellationToken.None);
        var replay = await fixture.Store.ReportAttemptProgressAsync(accepted.Id, request, CancellationToken.None);

        Assert.Equal(MarketAcquisitionAttemptEventResults.Accepted, first!.Result);
        Assert.Equal(MarketAcquisitionAttemptEventResults.Replayed, replay!.Result);
        await Assert.ThrowsAsync<MarketAcquisitionIdempotencyConflictException>(() =>
            fixture.Store.ReportAttemptProgressAsync(
                accepted.Id,
                request with { Message = "Different payload." },
                CancellationToken.None));
        await Assert.ThrowsAsync<MarketAcquisitionAttemptSequenceConflictException>(() =>
            fixture.Store.ReportAttemptProgressAsync(
                accepted.Id,
                request with { IdempotencyKey = "attempt-key-b", Message = "Different payload." },
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

    [Fact]
    public async Task RecordMarketObservationAsyncPersistsCompleteEvidenceAndReplaysIdempotently()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateAcceptedBatchAsync("market-observation-idempotent", lineCount: 1);
        var request = new MarketAcquisitionMarketObservationRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = "market-observation-key",
            AttemptId = "attempt-1",
            Sequence = 2,
            LineId = claimed.Lines[0].LineId,
            ItemId = claimed.Lines[0].ItemId,
            ItemName = claimed.Lines[0].ItemName,
            DataCenter = "Aether",
            WorldName = "Siren",
            ReadState = "Complete",
            ReportedListingCount = 1,
            ListingCapacity = 100,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Listings =
            [
                new MarketAcquisitionMarketObservationListing
                {
                    ListingId = "listing-1",
                    RetainerId = "retainer-1",
                    RetainerName = "Seller",
                    Quantity = 10,
                    UnitPrice = 50,
                },
            ],
        };

        var first = await fixture.Store.RecordMarketObservationAsync(claimed.Id, request, CancellationToken.None);
        var second = await fixture.Store.RecordMarketObservationAsync(claimed.Id, request, CancellationToken.None);
        var timeline = await fixture.Store.GetTimelineAsync(claimed.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ObservationId, second.ObservationId);
        var observed = Assert.Single(timeline!.MarketObservations);
        Assert.Equal("Complete", observed.ReadState);
        Assert.Equal("listing-1", Assert.Single(observed.Listings).ListingId);
    }

    [Fact]
    public async Task UnclaimedWorkOrderRemainsInInboxAfterLegacyPickupDeadline()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateBatchAsync(CreateBatchRequest("durable-inbox", expiresInSeconds: 1), CancellationToken.None);

        await BackdateAsync(
            fixture,
            created.Request.Id,
            "UPDATE acquisition_requests SET expires_at_utc = $past WHERE id = $id;");

        var pending = await fixture.Store.ListPendingAsync(MarketAcquisitionTestApp.CharacterName, MarketAcquisitionTestApp.WorldName, CancellationToken.None);
        Assert.Contains(pending, request => request.Id == created.Request.Id);
        var workOrder = await fixture.Store.GetWorkOrderAsync(created.Request.Id, CancellationToken.None);
        Assert.Equal(MarketAcquisitionWorkOrderStates.Inbox, workOrder!.State);
    }

    [Fact]
    public async Task ExpiredClaimReleasesRequestAndInvalidatesOldToken()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateClaimedBatchAsync("expired-claim");
        await BackdateAsync(
            fixture,
            claimed.Id,
            "UPDATE acquisition_requests SET claim_expires_at_utc = $past WHERE id = $id;");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Store.AcceptAsync(
                claimed.Id,
                new() { ClaimToken = claimed.ClaimToken, IdempotencyKey = "expired-accept" },
                CancellationToken.None));
        var reclaimed = await fixture.Store.ClaimAsync(
            claimed.Id,
            new()
            {
                CharacterName = MarketAcquisitionTestApp.CharacterName,
                World = MarketAcquisitionTestApp.WorldName,
                PluginInstanceId = "plugin-recovery",
            },
            CancellationToken.None);

        Assert.NotNull(reclaimed);
        Assert.NotEqual(claimed.ClaimToken, reclaimed.ClaimToken);
    }

    [Fact]
    public async Task StaleAcceptedRequestWithoutExecutionEvidenceReturnsToPendingPickup()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("stale-accepted");
        await BackdateExecutionAsync(fixture, accepted.Id);

        var pending = await fixture.Store.ListPendingAsync(
            MarketAcquisitionTestApp.CharacterName,
            MarketAcquisitionTestApp.WorldName,
            CancellationToken.None);

        var request = Assert.Single(pending, request => request.Id == accepted.Id);
        Assert.Equal(MarketAcquisitionStatuses.PendingPickup, request.Status);
    }

    [Fact]
    public async Task StaleRunningRequestWithExecutionEvidenceRequiresRecoveryAndCanBeResent()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("stale-running");
        await fixture.Store.ReportProgressAsync(
            accepted.Id,
            new()
            {
                ClaimToken = accepted.ClaimToken,
                IdempotencyKey = "stale-running-progress",
                RunnerState = "Running",
                Message = "Execution started.",
            },
            CancellationToken.None);
        await BackdateExecutionAsync(fixture, accepted.Id);

        var timeline = await fixture.Store.GetTimelineAsync(accepted.Id, CancellationToken.None);
        Assert.Equal(MarketAcquisitionStatuses.RecoveryRequired, timeline!.Request.Status);

        var resent = await fixture.Store.ResendAsync(accepted.Id, CancellationToken.None);
        Assert.Equal(MarketAcquisitionStatuses.PendingPickup, resent!.Status);
    }

    [Fact]
    public async Task ShelfAndRestorePreserveIntentAndAdvanceRevision()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateBatchAsync(CreateBatchRequest("shelf-restore"), CancellationToken.None);

        var shelved = await fixture.Store.ShelfWorkOrderAsync(created.Request.Id, new() { ExpectedRevision = 1 }, CancellationToken.None);
        Assert.Equal(MarketAcquisitionWorkOrderStates.Shelved, shelved!.State);
        Assert.Equal(2, shelved.Revision);
        Assert.Empty(await fixture.Store.ListPendingAsync(MarketAcquisitionTestApp.CharacterName, MarketAcquisitionTestApp.WorldName, CancellationToken.None));

        var restored = await fixture.Store.RestoreWorkOrderAsync(created.Request.Id, new() { ExpectedRevision = 2 }, CancellationToken.None);
        Assert.Equal(MarketAcquisitionWorkOrderStates.Inbox, restored!.State);
        Assert.Equal(3, restored.Revision);
        Assert.Single(restored.Request.Lines);
    }

    [Fact]
    public async Task CloneCreatesFreshInboxWorkWithLineage()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateBatchAsync(CreateBatchRequest("clone-source"), CancellationToken.None);

        var clone = await fixture.Store.CloneWorkOrderAsync(created.Request.Id, new()
        {
            ExpectedRevision = created.Request.Revision,
            IdempotencyKey = "clone-copy",
            Title = "Reusable shard order",
        }, CancellationToken.None);

        Assert.NotNull(clone);
        Assert.NotEqual(created.Request.Id, clone.Id);
        Assert.Equal(created.Request.Id, clone.ParentWorkOrderId);
        Assert.Equal("Reusable shard order", clone.Title);
        Assert.Equal(MarketAcquisitionWorkOrderStates.Inbox, clone.State);
        Assert.Single(clone.Request.Lines);
    }

    [Fact]
    public async Task MergePreviewExposesDangerousPriceConflict()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var target = await fixture.Store.CreateBatchAsync(CreateBatchRequest("merge-price-target"), CancellationToken.None);
        var sourceRequest = CreateBatchRequest("merge-price-source") with
        {
            Lines = [CreateLine(2, "Fire Shard", "Crystal", maxUnitPrice: 120, maxQuantity: 500)],
        };
        var source = await fixture.Store.CreateBatchAsync(sourceRequest, CancellationToken.None);

        var preview = await fixture.Store.PreviewWorkOrderMergeAsync(target.Request.Id, source.Request.Id, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.False(preview.CanMerge);
        Assert.Contains(preview.Conflicts, conflict => conflict.Field == "item.2.maxUnitPrice");
    }

    [Fact]
    public async Task MergeAtomicallyAddsCompatibleLinesAndArchivesSource()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var target = await fixture.Store.CreateBatchAsync(CreateBatchRequest("merge-target"), CancellationToken.None);
        var sourceRequest = CreateBatchRequest("merge-source") with
        {
            Lines = [CreateLine(4, "Lightning Shard", "Crystal", maxUnitPrice: 25, maxQuantity: 200)],
        };
        var source = await fixture.Store.CreateBatchAsync(sourceRequest, CancellationToken.None);

        var merged = await fixture.Store.MergeWorkOrdersAsync(target.Request.Id, new()
        {
            SourceWorkOrderId = source.Request.Id,
            ExpectedTargetRevision = target.Request.Revision,
            ExpectedSourceRevision = source.Request.Revision,
        }, CancellationToken.None);

        Assert.NotNull(merged);
        Assert.Equal(2, merged.Request.Lines.Count);
        Assert.Equal(2, merged.Revision);
        var archived = await fixture.Store.GetWorkOrderAsync(source.Request.Id, CancellationToken.None);
        Assert.Equal(MarketAcquisitionWorkOrderStates.Archived, archived!.State);
        Assert.Equal(2, archived.Revision);
    }

    [Fact]
    public async Task AcceptedExecutionHasRenewableLeaseSnapshotAndTerminalReceipt()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var accepted = await fixture.CreateAcceptedBatchAsync("execution-artifacts");

        var renewed = await fixture.Store.RenewLeaseAsync(accepted.Id, new()
        {
            ClaimToken = accepted.ClaimToken,
            PluginInstanceId = MarketAcquisitionTestApp.PluginInstanceId,
        }, CancellationToken.None);
        Assert.NotNull(renewed);
        Assert.True(renewed.ExpiresAtUtc > renewed.RenewedAtUtc);

        await fixture.Store.CompleteAsync(accepted.Id, new()
        {
            ClaimToken = accepted.ClaimToken,
            IdempotencyKey = "execution-artifacts-complete",
            Message = "Nothing remained to buy.",
        }, CancellationToken.None);

        var history = await fixture.Store.GetWorkOrderHistoryAsync(accepted.Id, CancellationToken.None);
        Assert.NotNull(history);
        Assert.Single(history.ExecutionSnapshots);
        var receipt = Assert.Single(history.Receipts);
        Assert.Equal("Completed", receipt.Outcome);
        Assert.Equal("Nothing remained to buy.", receipt.Message);
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

    private static MarketAcquisitionAttemptEventRequest CreateAttemptProgress(
        string claimToken,
        string idempotencyKey,
        string message) =>
        new()
        {
            ClaimToken = claimToken,
            IdempotencyKey = idempotencyKey,
            PluginInstanceId = MarketAcquisitionTestApp.PluginInstanceId,
            AttemptId = "attempt-1",
            EventSequence = 1,
            EventType = "progress",
            Phase = "Traveling",
            RunnerState = "Running",
            Message = message,
            ClientTimestampUtc = DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
        };

    private static async Task BackdateExecutionAsync(MarketAcquisitionStoreFixture fixture, string id) =>
        await BackdateAsync(
            fixture,
            id,
            """
            UPDATE acquisition_requests SET claimed_at_utc = $past WHERE id = $id;
            UPDATE acquisition_request_events SET created_at_utc = $past WHERE request_id = $id;
            """);

    private static async Task BackdateAsync(
        MarketAcquisitionStoreFixture fixture,
        string id,
        string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$past", DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
