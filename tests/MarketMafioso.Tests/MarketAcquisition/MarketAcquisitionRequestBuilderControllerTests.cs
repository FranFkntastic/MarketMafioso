using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestBuilderControllerTests
{
    [Fact]
    public async Task SelectedLineMutation_ClearsPendingRemoteAndPersistsLocalEdit()
    {
        var initial = CreateDocument() with
        {
            LocalRevision = 4,
            RemoteRequestId = "request-1",
            RemoteRevision = 2,
            SyncStatus = "SyncedClean",
        };
        var remote = initial with { RemoteRevision = 3, RemoteHash = "remote-3" };
        var persisted = new List<MarketAcquisitionRequestDocument>();
        var controller = CreateController(
            initial,
            refresh: document => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                document with { SyncStatus = "RemoteChanged" },
                remote,
                null,
                "Server changed.")),
            persist: persisted.Add);

        await controller.RefreshAsync();
        Assert.NotNull(controller.PendingRemoteDocument);

        Assert.True(controller.SetLineMaxUnitPrice(0, 450, "Unit cost ceiling updated."));

        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal(5, controller.Document.LocalRevision);
        Assert.Equal("LocalEdits", controller.Document.SyncStatus);
        Assert.Equal(0, controller.SelectedLineIndex);
        Assert.Null(controller.PendingRemoteDocument);
        Assert.Null(controller.PendingRemoteRequest);
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
        Assert.Equal(-1, controller.SelectedLineIndex);
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
        Assert.Equal("Request saved. Local edits made during sync were preserved.", controller.Status);
    }

    [Fact]
    public async Task RefreshThenAdoptRemote_PersistsAndNotifiesWorkspace()
    {
        var initial = CreateDocument() with { RemoteRequestId = "request-1", RemoteRevision = 1 };
        var remoteRequest = CreateRequestView(revision: 2);
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remoteRequest);
        var persisted = new List<MarketAcquisitionRequestDocument>();
        (MarketAcquisitionRequestDocument Document, MarketAcquisitionRequestView? Request)? adopted = null;
        var controller = CreateController(
            initial,
            refresh: document => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(
                document with { SyncStatus = "RemoteChanged" },
                remoteDocument,
                remoteRequest,
                "Server changed.")),
            adopted: (document, request) => adopted = (document, request),
            persist: persisted.Add);

        await controller.RefreshAsync();
        Assert.True(controller.AdoptRemote());

        Assert.Same(remoteDocument, controller.Document);
        Assert.Null(controller.PendingRemoteDocument);
        Assert.Equal(-1, controller.SelectedLineIndex);
        Assert.Equal("Loaded server copy.", controller.Status);
        Assert.NotNull(adopted);
        Assert.Same(remoteDocument, adopted.Value.Document);
        Assert.Same(remoteRequest, adopted.Value.Request);
        Assert.Same(remoteDocument, persisted[^1]);
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
            null,
            "Remote request refreshed and adopted."));

        await refreshTask;

        Assert.Equal(450u, controller.Document.Lines[0].MaxUnitPrice);
        Assert.Equal(2, controller.Document.RemoteRevision);
        Assert.Equal("RemoteChanged", controller.Document.SyncStatus);
        Assert.Same(remoteDocument, controller.PendingRemoteDocument);
        Assert.Equal("Remote request refreshed and adopted. Local edits made during refresh were preserved.", controller.Status);
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
