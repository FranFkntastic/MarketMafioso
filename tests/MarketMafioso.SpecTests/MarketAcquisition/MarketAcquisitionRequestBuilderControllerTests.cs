using System.Net;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRequestBuilderControllerTests
{
    [Fact]
    public void AddEditorLine_AppendsWithoutReplacingSelectedLine()
    {
        var existing = Line(36183, "Rose Gold Ingot") with { MaxUnitPrice = 999 };
        var added = Line(7017, "Varnish") with { MaxUnitPrice = 1_600 };
        var controller = CreateController(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
            {
                Lines = [existing],
                SyncStatus = "SyncedClean",
            });
        Assert.True(controller.SelectLine(0));

        controller.AddEditorLine(added);

        Assert.Equal(2, controller.Document.Lines.Count);
        Assert.Equal(existing, controller.Document.Lines[0]);
        Assert.Equal(added, controller.Document.Lines[1]);
        Assert.Equal(1, controller.SelectedLineIndex);
    }

    [Fact]
    public void Selection_ReusesExistingItemAndBuildsSharedActionPresentation()
    {
        var existing = Line(36183, "Rose Gold Ingot");
        var controller = CreateController(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
            {
                Lines = [existing],
                SyncStatus = "SyncedClean",
            });

        controller.ApplyEditorLine(existing with { MaxUnitPrice = 999 });

        Assert.Single(controller.Document.Lines);
        Assert.Equal(0, controller.SelectedLineIndex);
        Assert.Contains("already in the Workbench", controller.Status);

        var appraisalState = new CraftAppraisalRequestBuilderState();
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(controller.Document, existing);
        appraisalState.RecordLineQuote(
            identity,
            new CraftAppraisalQuote
            {
                ItemId = existing.ItemId,
                ItemName = existing.ItemName,
                EstimatedUnitCost = 576.4m,
                EstimatedTotalCost = 576.4m,
                IsComplete = true,
                Source = "CraftArchitectLocal",
                Confidence = "Medium",
                PlanId = "plan-1",
                PlanUrl = "https://example.test/?appraisalPlan=plan-1",
                Warnings = ["Retained evidence used."],
            },
            diagnosticFilePath: null);

        var presentation = Assert.IsType<MarketAcquisitionSelectedLinePresentation>(
            MarketAcquisitionSelectedLinePresenter.Build(
                controller.Document,
                controller.SelectedLineIndex,
                appraisalState,
                canEdit: true,
                isAppraising: false,
                isExactAcquisitionLine: false,
                DateTimeOffset.UtcNow));

        Assert.Equal("CA · 577 gil · 1 warning", presentation.EvidenceSummary);
        Assert.Equal(
            [
                MarketAcquisitionSelectedLineActionKind.RefreshQuote,
                MarketAcquisitionSelectedLineActionKind.UseQuote,
                MarketAcquisitionSelectedLineActionKind.OpenPlan,
                MarketAcquisitionSelectedLineActionKind.RemoveLine,
            ],
            presentation.Actions.Select(action => action.Kind));
        Assert.Equal(
            MarketAcquisitionSelectedLineSurface.CommandBar,
            MarketAcquisitionSelectedLinePresenter.ResolveSurface(inspectorRequested: false, presentation));
        Assert.Equal(
            MarketAcquisitionSelectedLineSurface.Inspector,
            MarketAcquisitionSelectedLinePresenter.ResolveSurface(inspectorRequested: true, presentation));
    }

    [Fact]
    public async Task ConflictPausesAutomaticSynchronizationInsteadOfRetrying()
    {
        var attempts = 0;
        var document = MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
        {
            Lines = [Line(36183, "Rose Gold Ingot")],
            RemoteRequestId = "request-1",
            RemoteRevision = 3,
            SyncStatus = "LocalEdits",
        };
        var controller = new MarketAcquisitionRequestBuilderController(
            document,
            _ =>
            {
                attempts++;
                throw new MarketAcquisitionLifecycleHttpException(
                    HttpStatusCode.Conflict,
                    "replace",
                    "revision changed",
                    null);
            },
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            _ => { });

        await controller.SyncAsync("Wei Ning", "Gilgamesh");
        controller.PumpAutomaticSynchronization(
            "Wei Ning",
            "Gilgamesh",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));
        await Task.Yield();

        Assert.Equal(1, attempts);
        Assert.Equal("RemoteChanged", controller.Document.SyncStatus);
        Assert.Contains("hosted sync is paused", controller.Status);
    }

    [Fact]
    public async Task FailedHostedSync_PausesWithoutRetryingAndLeavesLocalDraftAvailable()
    {
        var attempts = 0;
        var controller = new MarketAcquisitionRequestBuilderController(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
            {
                Lines = [Line(36183, "Rose Gold Ingot")],
                SyncStatus = "LocalEdits",
            },
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("offline");
            },
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            _ => { });

        controller.RequestHostedSync();
        await controller.SyncAsync("Wei Ning", "Gilgamesh");
        controller.PumpAutomaticSynchronization(
            "Wei Ning",
            "Gilgamesh",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(1, attempts);
        Assert.False(controller.IsHostedSyncEnabled);
        Assert.Equal("SyncFailed", controller.Document.SyncStatus);
        Assert.Contains("retry it explicitly or continue locally", controller.Status);
        Assert.Single(controller.Document.Lines);
    }

    [Fact]
    public void PersistedSyncFailure_DoesNotRearmHostedSyncOnRestore()
    {
        var attempts = 0;
        var controller = new MarketAcquisitionRequestBuilderController(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
            {
                Lines = [Line(36183, "Rose Gold Ingot")],
                RemoteRequestId = "request-1",
                RemoteRevision = 4,
                SyncStatus = "SyncFailed",
            },
            _ =>
            {
                attempts++;
                return Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(
                    MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh"),
                    "synced"));
            },
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            _ => { });

        controller.PumpAutomaticSynchronization(
            "Wei Ning",
            "Gilgamesh",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(0, attempts);
        Assert.False(controller.IsHostedSyncEnabled);
        Assert.Equal("SyncFailed", controller.Document.SyncStatus);
    }

    [Fact]
    public void FailedDraft_StartsBlankLocallyAndPreservesTheHostedDraftForRecovery()
    {
        var config = new Configuration();
        var current = MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
        {
            Lines = [Line(36183, "Rose Gold Ingot")],
            RemoteRequestId = "request-1",
            RemoteRevision = 4,
            SyncStatus = "SyncFailed",
        };
        MarketAcquisitionRequestDocumentPersistence.Save(config, current);

        var controller = new MarketAcquisitionRequestBuilderController(
            current,
            value => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(value, "synced")),
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            value => MarketAcquisitionRequestDocumentPersistence.Save(config, value),
            config);

        controller.StartBlankWorkbench("Wei Ning", "Gilgamesh");

        Assert.Empty(controller.Document.Lines);
        Assert.Null(controller.Document.RemoteRequestId);
        Assert.Equal("NewDraft", controller.Document.SyncStatus);
        var recovery = Assert.IsType<MarketAcquisitionRequestDocument>(
            MarketAcquisitionRequestDocumentPersistence.RestorePrevious(config));
        Assert.Equal("request-1", recovery.RemoteRequestId);
        Assert.Equal("SyncFailed", recovery.SyncStatus);
        Assert.False(controller.IsHostedSyncEnabled);
    }

    [Fact]
    public void DetachHostedAssociation_ClearsOnlyLocalMetadataAndTellsTheTruth()
    {
        MarketAcquisitionRequestDocument? persisted = null;
        var controller = new MarketAcquisitionRequestBuilderController(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
            {
                Lines = [Line(36183, "Rose Gold Ingot")],
                RemoteRequestId = "request-1",
                RemoteRevision = 4,
                SyncStatus = "SyncFailed",
            },
            value => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(value, "synced")),
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            value => persisted = value);

        Assert.True(controller.DetachHostedAssociation());

        Assert.Null(controller.Document.RemoteRequestId);
        Assert.Equal(0, controller.Document.RemoteRevision);
        Assert.Equal("NewDraft", controller.Document.SyncStatus);
        Assert.Single(controller.Document.Lines);
        Assert.Contains("server copy was not shelved or changed", controller.Status);
        Assert.Equal(controller.Document, persisted);
    }

    [Fact]
    public void LoadComposition_StaysLocalUntilHostedSyncIsRequested()
    {
        var config = new Configuration();
        var current = MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
        {
            Lines = [Line(36183, "Rose Gold Ingot")],
            RemoteRequestId = "request-1",
            RemoteRevision = 4,
            SyncStatus = "SyncedClean",
        };
        MarketAcquisitionRequestDocumentPersistence.Save(config, current);
        var attempts = 0;
        var controller = new MarketAcquisitionRequestBuilderController(
            current,
            value =>
            {
                attempts++;
                return Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(value, "synced"));
            },
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            value => MarketAcquisitionRequestDocumentPersistence.Save(config, value),
            config);

        controller.LoadComposition(
            new MarketAcquisitionWorkbenchComposition
            {
                Name = "Local composition",
                Lines = [Line(7017, "Varnish")],
            },
            "Wei Ning",
            "Gilgamesh");
        controller.PumpAutomaticSynchronization(
            "Wei Ning",
            "Gilgamesh",
            canSynchronize: true,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(7017u, Assert.Single(controller.Document.Lines).ItemId);
        Assert.Equal(0, attempts);
        Assert.False(controller.IsHostedSyncEnabled);
    }

    [Fact]
    public void Finalize_PersistenceFailureLeavesAuthorityUnfinalizedAndRetryable()
    {
        var attempts = 0;
        MarketAcquisitionRequestDocument? persisted = null;
        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Siren"),
            ExactAcquisitionWorkbenchAuthorityTests.Transfer());
        var controller = new MarketAcquisitionRequestBuilderController(
            staged,
            value => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(value, "synced")),
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            value =>
            {
                attempts++;
                if (attempts == 1)
                    throw new UnauthorizedAccessException("Access is denied.");

                persisted = value;
            });

        Assert.False(controller.FinalizeExactAcquisitionAuthority());

        Assert.Null(controller.Document.ExactAcquisitionAuthority!.FinalizedContract);
        Assert.Contains("remains unfinalized", controller.Status);
        Assert.Contains("retry Finalize", controller.Status);

        Assert.True(controller.FinalizeExactAcquisitionAuthority());

        Assert.NotNull(controller.Document.ExactAcquisitionAuthority!.FinalizedContract);
        Assert.Equal(controller.Document, persisted);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void PreviousWorkbenchPersistenceRestoresContentWithoutRemoteIdentity()
    {
        var config = new Configuration();
        var current = MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh") with
        {
            Lines = [Line(36183, "Rose Gold Ingot")],
            RemoteRequestId = "request-1",
            RemoteRevision = 7,
            SyncStatus = "SyncedClean",
        };

        MarketAcquisitionRequestDocumentPersistence.SavePrevious(config, current);
        var restored = Assert.IsType<MarketAcquisitionRequestDocument>(
            MarketAcquisitionRequestDocumentPersistence.RestorePrevious(config)).WithNewIdentity();

        Assert.Equal(36183u, Assert.Single(restored.Lines).ItemId);
        Assert.Null(restored.RemoteRequestId);
        Assert.Equal(0, restored.RemoteRevision);
        Assert.Equal("NewDraft", restored.SyncStatus);
    }

    private static MarketAcquisitionRequestBuilderController CreateController(
        MarketAcquisitionRequestDocument document) =>
        new(
            document,
            value => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(value, "synced")),
            value => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(value, null, "refreshed")),
            (_, _) => { },
            _ => { });

    private static MarketAcquisitionRequestLineDocument Line(uint itemId, string name) =>
        new()
        {
            ItemId = itemId,
            ItemName = name,
            QuantityMode = "AllBelowThreshold",
            HqPolicy = "Either",
            MaxUnitPrice = 500,
            GilCap = 5000,
        };
}
