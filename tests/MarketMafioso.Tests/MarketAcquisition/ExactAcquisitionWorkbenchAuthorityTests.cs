using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;

using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class ExactAcquisitionWorkbenchAuthorityTests
{
    [Fact]
    public void Stage_ReplacesConflictingItemAndPreservesManualWorkbenchLines()
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault() with
        {
            Lines =
            [
                new() { ItemId = 1, ItemName = "Manual item", HqPolicy = "Either" },
                new() { ItemId = 10, ItemName = "Old ambiguous ring", HqPolicy = "Either" },
            ],
        };

        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(document, Transfer(), 10);

        Assert.Equal(2, staged.Lines.Count);
        Assert.Contains(staged.Lines, line => line.ItemId == 1 && line.HqPolicy == "Either");
        var exact = Assert.Single(staged.Lines, line => line.ItemId == 10);
        Assert.Equal("HQOnly", exact.HqPolicy);
        Assert.Equal("TargetQuantity", exact.QuantityMode);
        Assert.Equal(2u, exact.TargetQuantity);
        Assert.Equal(110u, exact.MaxUnitPrice);
        Assert.Equal(220u, exact.GilCap);
        Assert.Equal(2, staged.LocalRevision);
        Assert.Equal(220ul, staged.ExactAcquisitionAuthority!.PlanCapGil);
        Assert.Equal(ExactAcquisitionWorkbenchAuthority.CrossWorldExactQualityV1, staged.ExactAcquisitionAuthority.RecoveryPolicyId);
    }

    [Fact]
    public void UpdatePriceFlex_DerivesAndPersistsFixedAbsoluteCaps()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());

        var updated = ExactAcquisitionWorkbenchAuthorityService.UpdatePriceFlex(staged, 25);

        var line = Assert.Single(updated.Lines);
        Assert.Equal(125u, line.MaxUnitPrice);
        Assert.Equal(250u, line.GilCap);
        Assert.Equal(250ul, updated.ExactAcquisitionAuthority!.PlanCapGil);
        Assert.Equal(25, updated.ExactAcquisitionAuthority.PriceFlexPercent);
        Assert.Null(updated.ExactAcquisitionAuthority.FinalizedContract);
    }

    [Fact]
    public void Stage_PreservesCraftingMaterialKindThroughVisibleWorkbenchLine()
    {
        var transfer = Transfer();
        transfer = transfer with
        {
            MarketLots = transfer.MarketLots.Select(lot => lot with { ItemKind = "Crafting material" }).ToArray(),
        };

        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(),
            transfer);

        var line = Assert.Single(staged.Lines);
        Assert.Equal("Crafting material", line.ItemKind);
        Assert.Equal("Crafting material", Assert.Single(staged.ExactAcquisitionAuthority!.Lines).ItemKind);
    }

    [Fact]
    public void SemanticEdit_InvalidatesLineageButRetainsHistoricalTransfer()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());
        var changed = staged with
        {
            Lines = [staged.Lines[0] with { HqPolicy = "NQOnly" }],
            LocalRevision = staged.LocalRevision + 1,
        };

        var reconciled = ExactAcquisitionWorkbenchAuthorityService.ReconcileEdit(staged, changed);

        Assert.Equal(ExactAcquisitionWorkbenchLineageState.Invalidated, reconciled.ExactAcquisitionAuthority!.LineageState);
        Assert.Equal("selected-solution", reconciled.ExactAcquisitionAuthority.Transfer.SelectedSolutionId);
        Assert.Contains("missing or duplicated", reconciled.ExactAcquisitionAuthority.InvalidationReason, StringComparison.Ordinal);
        Assert.False(ExactAcquisitionWorkbenchAuthorityService.ValidateForFinalization(reconciled).IsValid);
    }

    [Fact]
    public void HiddenPricingMismatch_CannotMasqueradeAsVisibleApprovalEnvelope()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());
        var changed = staged with { Lines = [staged.Lines[0] with { MaxUnitPrice = 999 }] };
        var reconciled = ExactAcquisitionWorkbenchAuthorityService.ReconcileEdit(staged, changed);

        Assert.True(reconciled.ExactAcquisitionAuthority!.IsLineageValid);
        var validation = ExactAcquisitionWorkbenchAuthorityService.ValidateForFinalization(reconciled);
        Assert.False(validation.IsValid);
        Assert.Contains("approval envelope", validation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_BindsVersionedVisibleAuthorityToDocumentRevisionAndIntent()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer(), 15);

        var finalized = ExactAcquisitionWorkbenchAuthorityService.Finalize(staged);
        var contract = Assert.IsType<ExactAcquisitionExecutionContract>(finalized.ExactAcquisitionAuthority!.FinalizedContract);

        Assert.Equal(staged.LocalRequestId, contract.WorkbenchDocumentId);
        Assert.Equal(staged.LocalRevision, contract.WorkbenchRevision);
        Assert.Equal(ExactAcquisitionWorkbenchAuthority.CrossWorldExactQualityV1, contract.RecoveryPolicyId);
        Assert.Equal(230ul, contract.PlanCapGil);
        Assert.Equal(ExactAcquisitionWorkbenchAuthorityService.ComputeCanonicalIntentHash(staged), contract.CanonicalIntentHash);
        Assert.Equal(ExactAcquisitionExecutionContract.CurrentSchemaVersion, contract.SchemaVersion);
        Assert.Equal(staged.TargetCharacterName, contract.TargetCharacterName);
        Assert.Equal(staged.TargetWorld, contract.TargetWorld);

        var repeated = ExactAcquisitionWorkbenchAuthorityService.Finalize(finalized);
        Assert.Same(finalized, repeated);
    }

    [Fact]
    public void Finalize_ReplacesLegacyContractThatLacksExplicitWorldAuthority()
    {
        var finalized = ExactAcquisitionWorkbenchAuthorityService.Finalize(
            ExactAcquisitionWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren"), Transfer()));
        var current = finalized.ExactAcquisitionAuthority!.FinalizedContract!;
        var legacy = current with
        {
            SchemaVersion = "marketmafioso-squire-outfitter-execution-contract/v1",
            AuthorizedWorlds = [],
        };
        finalized = finalized with
        {
            ExactAcquisitionAuthority = finalized.ExactAcquisitionAuthority with { FinalizedContract = legacy },
        };

        var upgraded = ExactAcquisitionWorkbenchAuthorityService.Finalize(finalized);
        var contract = upgraded.ExactAcquisitionAuthority!.FinalizedContract!;

        Assert.Equal(ExactAcquisitionExecutionContract.CurrentSchemaVersion, contract.SchemaVersion);
        Assert.NotEqual(legacy.ContractId, contract.ContractId);
        Assert.NotEmpty(contract.AuthorizedWorlds);
    }

    [Fact]
    public void Persistence_RoundTripsAuthorityWhileServerIntentHashRemainsBuyListOnly()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Finalize(
            ExactAcquisitionWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault(), Transfer(), 5));
        var config = new Configuration();

        MarketAcquisitionRequestDocumentPersistence.Save(config, staged);
        var restored = MarketAcquisitionRequestDocumentPersistence.Restore(config);

        var restoredAuthority = Assert.IsType<ExactAcquisitionWorkbenchAuthority>(restored.ExactAcquisitionAuthority);
        var stagedAuthority = Assert.IsType<ExactAcquisitionWorkbenchAuthority>(staged.ExactAcquisitionAuthority);
        Assert.NotNull(restoredAuthority.FinalizedContract);
        Assert.Equal("selected-solution", restoredAuthority.Transfer.SelectedSolutionId);
        Assert.Equal(stagedAuthority.PlanCapGil, restoredAuthority.PlanCapGil);
        Assert.Equal(
            MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(staged with { ExactAcquisitionAuthority = null }),
            MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(staged));
    }

    [Fact]
    public async Task RemoteRefresh_PreservesHistoryButInvalidatesChangedExactSolution()
    {
        var staged = ExactAcquisitionWorkbenchAuthorityService.Finalize(
            ExactAcquisitionWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault(), Transfer()));
        var remote = new MarketAcquisitionRequestView
        {
            Id = "request-1",
            Revision = 2,
            Region = "North America",
            WorldMode = "Recommended",
            SweepScope = "Region",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 1,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                },
            ],
        };
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remote);
        var initial = staged with { RemoteRequestId = remote.Id, RemoteRevision = 1, SyncStatus = "SyncedClean" };
        var controller = new MarketAcquisitionRequestBuilderController(
            initial,
            current => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(current, string.Empty)),
            _ => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(remoteDocument, remote, "refreshed")),
            (_, _) => { },
            _ => { });

        await controller.RefreshAsync();

        var authority = Assert.IsType<ExactAcquisitionWorkbenchAuthority>(controller.Document.ExactAcquisitionAuthority);
        Assert.Equal("selected-solution", authority.Transfer.SelectedSolutionId);
        Assert.Equal(ExactAcquisitionWorkbenchLineageState.Invalidated, authority.LineageState);
        Assert.Null(authority.FinalizedContract);
    }

    internal static ExactAcquisitionWorkbenchTransfer Transfer()
    {
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var offerKey = new EquipmentOfferKey(
            10,
            EquipmentQuality.High,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:test:10:High");
        return new(
            ExactAcquisitionWorkbenchTransfer.CurrentSchemaVersion,
            ExactAcquisitionWorkbenchTransfer.ExternalPlanningOrigin,
            "selected-solution",
            "nomination",
            new("test-profile", "v1"),
            new("ordinary", 16, 100, "ordinary nodes", ["gathering"]),
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, "evidence/v1", "universalis", "North America", "ExhaustiveWithinScope", now),
            [new(EquipmentLoadoutPosition.LeftRing, offerKey, 2, "listing-1", "Market board - Siren")],
            [new(offerKey, "Exact HQ Ring", 2, 2, "Siren", 100, 200, "listing-1", "source-r1", now, "Retainer", "retainer-1")],
            200);
    }
}
