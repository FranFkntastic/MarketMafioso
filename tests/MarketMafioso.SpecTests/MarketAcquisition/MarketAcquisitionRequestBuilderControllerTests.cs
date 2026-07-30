using System.Net;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
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
        Assert.Contains("automatic sync is paused", controller.Status);
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
