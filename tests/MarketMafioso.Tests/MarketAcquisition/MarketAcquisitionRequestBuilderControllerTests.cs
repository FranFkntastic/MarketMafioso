using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Tests.Squire;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestBuilderControllerTests
{
    [Fact]
    public void EnsureCharacterScope_CreatesNewRevisionAndClearsFinalizedConfirmation()
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        document = OutfitterWorkbenchAuthorityService.Stage(document, OutfitterWorkbenchAuthorityTests.Transfer());
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var originalRevision = document.LocalRevision;
        var controller = CreateController(document);

        controller.EnsureCharacterScope("Fran", "Midgardsormr");

        Assert.Equal(originalRevision + 1, controller.Document.LocalRevision);
        Assert.Equal("Midgardsormr", controller.Document.TargetWorld);
        Assert.True(controller.Document.OutfitterAuthority!.IsLineageValid);
        Assert.Null(controller.Document.OutfitterAuthority.FinalizedContract);
    }

    [Fact]
    public async Task SelectedLineMutation_UsesLatestServerCopyAndPersistsLocalEdit()
    {
        var initial = CreateDocument() with
        {
            LocalRevision = 4,
            RemoteRequestId = "request-1",
            RemoteRevision = 2,
            SyncStatus = "SyncedClean",
        };
        var remote = initial with { RemoteRevision = 3, RemoteHash = "remote-3", SyncStatus = "SyncedClean" };
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(
            initial,
            refresh: document => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                remote,
                null,
                "Server changed.")),
            persist: persisted.Add);

        await controller.RefreshAsync();
        Assert.Equal(remote.RemoteRevision, controller.Document.RemoteRevision);
        Assert.Equal(remote.RemoteHash, controller.Document.RemoteHash);
        Assert.Equal(initial.LocalRequestId, controller.Document.LocalRequestId);
        Assert.Equal(initial.LocalRevision, controller.Document.LocalRevision);

        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));

        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal(5, controller.Document.LocalRevision);
        Assert.Equal("LocalEdits", controller.Document.SyncStatus);
        Assert.Equal(0, controller.SelectedLineIndex);
        Assert.Equal("Unit cost ceiling updated.", controller.Status);
        Assert.Same(controller.Document, persisted[^1]);
    }

    [Fact]
    public async Task Sync_AppliesCharacterScopeAndOwnsSuccessTransition()
    {
        var initial = CreateDocument();
        MarketAcquisitionRequestDocument? submitted = null;
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var synced = initial with
        {
            RemoteRequestId = "request-1",
            RemoteRevision = 1,
            SyncStatus = "SyncedClean",
        };
        var controller = CreateController(
            initial,
            sync: document =>
            {
                submitted = document;
                return Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(synced, "Request saved."));
            },
            persist: persisted.Add);
        controller.SelectLine(0);

        await controller.SyncAsync("Eriana Ning", "Siren");

        Assert.NotNull(submitted);
        Assert.Equal("Eriana Ning", submitted.TargetCharacterName);
        Assert.Equal("Siren", submitted.TargetWorld);
        Assert.Same(synced, controller.Document);
        Assert.Equal(0, controller.SelectedLineIndex);
        Assert.Equal("Request saved.", controller.Status);
        Assert.False(controller.IsSyncing);
        Assert.Same(synced, persisted[^1]);
    }

    [Fact]
    public async Task Sync_PreservesEditsMadeWhileRequestIsInFlight()
    {
        var initial = CreateDocument();
        var completion = new TaskCompletionSource<MarketAcquisitionRequestBuilderSyncOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateController(initial, sync: _ => completion.Task);

        var syncTask = controller.SyncAsync("Eriana Ning", "Siren");
        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));
        completion.SetResult(new MarketAcquisitionRequestBuilderSyncOutcome(
            initial with
            {
                RemoteRequestId = "request-1",
                RemoteRevision = 3,
                RemoteOrigin = "Plugin",
                LastSyncedHash = "synced-hash",
                RemoteHash = "synced-hash",
                SyncStatus = "SyncedClean",
            },
            "Request saved."));

        await syncTask;

        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal("request-1", controller.Document.RemoteRequestId);
        Assert.Equal(3, controller.Document.RemoteRevision);
        Assert.Equal("synced-hash", controller.Document.LastSyncedHash);
        Assert.Equal("LocalEdits", controller.Document.SyncStatus);
        Assert.Equal(0, controller.SelectedLineIndex);
        Assert.Equal("Request saved. A newer local edit is queued for synchronization.", controller.Status);
    }

    [Fact]
    public async Task Refresh_AdoptsMostRecentServerCopyAndNotifiesWorkspace()
    {
        var initial = CreateDocument() with { RemoteRequestId = "request-1", RemoteRevision = 1 };
        var remoteRequest = CreateRequestView(revision: 2);
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest);
        var persisted = new List<MarketAcquisitionRequestDocument>();
        (MarketAcquisitionRequestDocument Document, MarketAcquisitionRequestView? Request)? adopted = null;
        var controller = CreateController(
            initial,
            refresh: document => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                remoteDocument,
                remoteRequest,
                "Server changed.")),
            adopted: (document, request) => adopted = (document, request),
            persist: persisted.Add);

        await controller.RefreshAsync();

        Assert.Equal(remoteDocument.RemoteRevision, controller.Document.RemoteRevision);
        Assert.Equal(remoteDocument.RemoteHash, controller.Document.RemoteHash);
        Assert.Equal(initial.LocalRequestId, controller.Document.LocalRequestId);
        Assert.Equal(initial.LocalRevision, controller.Document.LocalRevision);
        Assert.Equal(-1, controller.SelectedLineIndex);
        Assert.Equal("Server changed.", controller.Status);
        Assert.NotNull(adopted);
        Assert.Same(controller.Document, adopted.Value.Document);
        Assert.Same(remoteRequest, adopted.Value.Request);
        Assert.Same(controller.Document, persisted[^1]);
    }

    [Fact]
    public async Task Refresh_UnchangedRemoteSnapshot_IsACompletePersistenceNoOp()
    {
        var remoteRequest = CreateRequestView(revision: 2);
        var initial = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest) with
        {
            LocalRequestId = "stable-local-id",
            LocalRevision = 8,
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var reconstructed = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest);
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var adoptionCount = 0;
        var controller = CreateController(
            initial,
            refresh: _ => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                reconstructed,
                remoteRequest,
                "Request synchronized from the server.")),
            adopted: (_, _) => adoptionCount++,
            persist: persisted.Add);

        await controller.RefreshAsync();

        Assert.Same(initial, controller.Document);
        Assert.Equal("stable-local-id", controller.Document.LocalRequestId);
        Assert.Equal(8, controller.Document.LocalRevision);
        Assert.Empty(persisted);
        Assert.Equal(0, adoptionCount);
        Assert.Equal("Request synchronized from the server.", controller.Status);
    }

    [Fact]
    public async Task Refresh_PreservesEditsMadeWhileRequestIsInFlight()
    {
        var initial = CreateDocument() with { RemoteRequestId = "request-1", RemoteRevision = 1 };
        var completion = new TaskCompletionSource<MarketAcquisitionRequestBuilderRefreshOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteRequest = CreateRequestView(revision: 2);
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest);
        var controller = CreateController(initial, refresh: _ => completion.Task);

        var refreshTask = controller.RefreshAsync();
        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));
        completion.SetResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
            remoteDocument,
            null,
            "Remote request refreshed and adopted."));

        await refreshTask;

        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal(1, controller.Document.RemoteRevision);
        Assert.Equal("LocalEdits", controller.Document.SyncStatus);
        Assert.Equal("A newer local edit superseded the server refresh and is queued for synchronization.", controller.Status);
    }

    [Fact]
    public async Task CommittedEdit_IsAutomaticallySynchronizedByPump()
    {
        var initial = CreateDocument() with
        {
            RemoteRequestId = "request-1",
            RemoteRevision = 1,
            SyncStatus = "SyncedClean",
        };
        MarketAcquisitionRequestDocument? submitted = null;
        var controller = CreateController(
            initial,
            sync: document =>
            {
                submitted = document;
                return Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(
                    document with { RemoteRevision = 2, SyncStatus = "SyncedClean" },
                    "Request synchronized."));
            });

        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));
        controller.PumpAutomaticSynchronization(
            "Eriana Ning",
            "Siren",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await Task.Yield();

        Assert.NotNull(submitted);
        Assert.Equal(450u, submitted.Lines[0].MaxUnitPrice);
        Assert.Equal(2, controller.Document.RemoteRevision);
        Assert.Equal("SyncedClean", controller.Document.SyncStatus);
    }

    [Fact]
    public async Task CleanRequest_PollsAndAdoptsMostRecentServerRevision()
    {
        var initial = CreateDocument() with
        {
            RemoteRequestId = "request-1",
            RemoteRevision = 1,
            SyncStatus = "SyncedClean",
        };
        var remoteRequest = CreateRequestView(revision: 2);
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest);
        var controller = CreateController(
            initial,
            refresh: _ => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                remoteDocument,
                remoteRequest,
                "Request synchronized from the server.")));

        controller.PumpAutomaticSynchronization(
            "Eriana Ning",
            "Siren",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await Task.Yield();

        Assert.Equal(2, controller.Document.RemoteRevision);
        Assert.Equal(300u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal("SyncedClean", controller.Document.SyncStatus);
    }

    [Fact]
    public async Task IncompleteDraft_WaitsForRequiredCeilingBeforeAutomaticSync()
    {
        var initial = CreateDocument() with
        {
            Lines = [CreateDocument().Lines[0] with { MaxUnitPrice = 0 }],
            SyncStatus = "LocalEdits",
        };
        var syncCount = 0;
        var controller = CreateController(
            initial,
            sync: document =>
            {
                syncCount++;
                return Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(
                    document with { SyncStatus = "SyncedClean" },
                    "Request synchronized."));
            });

        controller.PumpAutomaticSynchronization(
            "Eriana Ning",
            "Siren",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await Task.Yield();

        Assert.Equal(0, syncCount);
        Assert.False(controller.DraftValidation.IsValid);

        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));
        controller.PumpAutomaticSynchronization(
            "Eriana Ning",
            "Siren",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await Task.Yield();

        Assert.Equal(1, syncCount);
        Assert.True(controller.DraftValidation.IsValid);
        Assert.Equal("SyncedClean", controller.Document.SyncStatus);
    }

    [Fact]
    public async Task AutomaticPoll_ExposesRefreshTaskForForegroundActionsToAwait()
    {
        var initial = CreateDocument() with
        {
            RemoteRequestId = "request-1",
            RemoteRevision = 1,
            SyncStatus = "SyncedClean",
        };
        var completion = new TaskCompletionSource<MarketAcquisitionRequestBuilderRefreshOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateController(initial, refresh: _ => completion.Task);

        controller.PumpAutomaticSynchronization(
            "Eriana Ning",
            "Siren",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(controller.IsRefreshing);
        Assert.False(controller.WaitForRefreshAsync().IsCompleted);

        completion.SetResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
            initial,
            null,
            "Request synchronized from the server."));
        await controller.WaitForRefreshAsync();

        Assert.False(controller.IsRefreshing);
    }

    [Fact]
    public void PricingOnlyEdit_PreservesUnrelatedLineFields()
    {
        var initial = CreateDocument() with
        {
            Lines =
            [
                CreateDocument().Lines[0] with
                {
                    QuantityMode = string.Empty,
                    HqPolicy = string.Empty,
                },
            ],
        };
        var controller = CreateController(initial);

        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));

        Assert.Equal(string.Empty, controller.Document.Lines[0].QuantityMode);
        Assert.Equal(string.Empty, controller.Document.Lines[0].HqPolicy);
        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
    }

    [Fact]
    public void RestoredRemote_DoesNotOverwriteLocalEditsForSameRequest()
    {
        var initial = CreateDocument() with
        {
            RemoteRequestId = "request-1",
            RemoteRevision = 1,
            SyncStatus = "LocalEdits",
        };
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(initial, persist: persisted.Add);

        var adopted = controller.AdoptRestoredRequestIfSafe(CreateRequestView(revision: 2));

        Assert.False(adopted);
        Assert.Same(initial, controller.Document);
        Assert.Empty(persisted);
    }

    [Fact]
    public void MarkPlanPrepared_PersistsWithoutAdvancingEditRevision()
    {
        var initial = CreateDocument() with { LocalRevision = 7 };
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(initial, persist: persisted.Add);

        controller.MarkPlanPrepared("plan-hash");

        Assert.Equal("plan-hash", controller.Document.LastPlanHash);
        Assert.Equal(7, controller.Document.LocalRevision);
        Assert.Same(controller.Document, persisted[^1]);
    }

    [Fact]
    public void ScopeAddAndRemove_UseCanonicalControllerTransition()
    {
        var document = CreateDocument() with
        {
            LocalRevision = 10,
            RemoteRequestId = "request-1",
            SyncStatus = "SyncedClean",
        };
        var scope = RequestRouteScope.FromDocument(document) with
        {
            WorldMode = "AllWorldSweep",
            SweepScope = "Region",
        };

        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(document, persist: persisted.Add);

        controller.UpdateRouteScope(scope);
        controller.ApplyEditorLine(new MarketAcquisitionRequestLineDocument
        {
            ItemId = 2,
            ItemName = "Fire Shard",
        });
        Assert.True(controller.RemoveLine(1));

        Assert.Equal(13, controller.Document.LocalRevision);
        Assert.Equal("LocalEdits", controller.Document.SyncStatus);
        Assert.Equal("AllWorldSweep", controller.Document.WorldMode);
        Assert.Single(controller.Document.Lines);
        Assert.Equal(-1, controller.SelectedLineIndex);
        Assert.Equal("Line removed.", controller.Status);
        Assert.Equal(3, persisted.Count);
    }

    [Fact]
    public void AddLines_DeduplicatesExistingAndIncomingItemsInOnePersistedEdit()
    {
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(CreateDocument(), persist: persisted.Add);

        var added = controller.AddLines([
            new() { ItemId = 19951, ItemName = "Koppranickel Ore" },
            new() { ItemId = 100, ItemName = "Bronze Sallet", ItemKind = "Equipment" },
            new() { ItemId = 100, ItemName = "Bronze Sallet duplicate", ItemKind = "Equipment" },
        ]);

        Assert.Equal(1, added);
        Assert.Equal(2, controller.Document.Lines.Count);
        Assert.Equal("Bronze Sallet", controller.Document.Lines[1].ItemName);
        Assert.Equal("Added 1 Outfitter line.", controller.Status);
        Assert.Single(persisted);
    }

    [Fact]
    public void RemoveLinesByItemId_ReturnsOnlySelectedWorkbenchItemsInOnePersistedEdit()
    {
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(CreateDocument() with
        {
            Lines =
            [
                new() { ItemId = 1, ItemName = "Fire Shard" },
                new() { ItemId = 2, ItemName = "Ice Shard" },
                new() { ItemId = 3, ItemName = "Wind Shard" },
            ],
        }, persist: persisted.Add);

        var removed = controller.RemoveLinesByItemId([1, 3, 999]);

        Assert.Equal(2, removed);
        Assert.Equal([2u], controller.Document.Lines.Select(line => line.ItemId));
        Assert.Equal("Returned 2 items to the inbox selection.", controller.Status);
        Assert.Single(persisted);
    }

    [Fact]
    public void LoadComposition_ReplacesWorkbenchWithFreshLocalDraft()
    {
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(CreateDocument() with
        {
            RemoteRequestId = "remote-1",
            RemoteRevision = 9,
            LastPlanHash = "old-plan",
        }, persist: persisted.Add);
        var composition = MarketAcquisitionWorkbenchComposition.FromDocument(
            "Shard restock",
            MarketAcquisitionRequestDocument.CreateDefault() with
            {
                Region = "Europe",
                Lines = [new() { ItemId = 2, ItemName = "Fire Shard", TargetQuantity = 99 }],
            },
            DateTimeOffset.UtcNow);

        controller.LoadComposition(composition, "Wei Ning", "Siren");

        Assert.Equal("Wei Ning", controller.Document.TargetCharacterName);
        Assert.Equal("Siren", controller.Document.TargetWorld);
        Assert.Equal("Europe", controller.Document.Region);
        Assert.Equal([2u], controller.Document.Lines.Select(line => line.ItemId));
        Assert.Null(controller.Document.RemoteRequestId);
        Assert.Equal(0, controller.Document.RemoteRevision);
        Assert.Null(controller.Document.LastPlanHash);
        Assert.Equal("NewDraft", controller.Document.SyncStatus);
        Assert.Equal("Loaded Shard restock as a new Workbench draft.", controller.Status);
        Assert.Single(persisted);
    }

    [Fact]
    public void MergeComposition_PreservesWorkbenchLinesAndAddsOnlyMissingItems()
    {
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(CreateDocument(), persist: persisted.Add);
        var composition = MarketAcquisitionWorkbenchComposition.FromDocument(
            "Daily supplies",
            MarketAcquisitionRequestDocument.CreateDefault() with
            {
                Lines =
                [
                    new() { ItemId = 19951, ItemName = "Do not overwrite" },
                    new() { ItemId = 2, ItemName = "Fire Shard" },
                ],
            },
            DateTimeOffset.UtcNow);

        var added = controller.MergeComposition(composition);

        Assert.Equal(1, added);
        Assert.Equal([19951u, 2u], controller.Document.Lines.Select(line => line.ItemId));
        Assert.Equal("Koppranickel Ore", controller.Document.Lines[0].ItemName);
        Assert.Equal("Added 1 Daily supplies line.", controller.Status);
        Assert.Single(persisted);
    }

    private static MarketAcquisitionRequestBuilderController CreateController(
        MarketAcquisitionRequestDocument document,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>>? sync = null,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>>? refresh = null,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?>? adopted = null,
        Action<MarketAcquisitionRequestDocument>? persist = null) =>
        new(
            document,
            sync ?? (current => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(current, string.Empty))),
            refresh ?? (current => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                current,
                null,
                string.Empty))),
            adopted ?? ((_, _) => { }),
            persist ?? (_ => { }));

    private static MarketAcquisitionRequestDocument CreateDocument() =>
        MarketAcquisitionRequestDocument.CreateDefault() with
        {
            Lines =
            [
                new MarketAcquisitionRequestLineDocument
                {
                    ItemId = 19951,
                    ItemName = "Koppranickel Ore",
                    QuantityMode = "AllBelowThreshold",
                    MaxQuantity = 25,
                    HqPolicy = "Either",
                    MaxUnitPrice = 276,
                },
            ],
        };

    private static MarketAcquisitionRequestView CreateRequestView(int revision) => new()
    {
        Id = "request-1",
        Revision = revision,
        TargetCharacterName = "Eriana Ning",
        TargetWorld = "Siren",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
        Lines =
        [
            new MarketAcquisitionBatchLineView
            {
                ItemId = 19951,
                ItemName = "Koppranickel Ore",
                QuantityMode = "AllBelowThreshold",
                MaxQuantity = 25,
                HqPolicy = "Either",
                MaxUnitPrice = 300,
            },
        ],
    };
}
