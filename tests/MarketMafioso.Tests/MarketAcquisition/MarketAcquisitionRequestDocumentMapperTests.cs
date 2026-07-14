using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestDocumentMapperTests
{
    [Fact]
    public void BuildCreateRequest_UsesPluginBuilderOriginAndPreservesLines()
    {
        var document = CreateDocument();

        var request = MarketAcquisitionRequestDocumentMapper.BuildCreateRequest(
            document,
            "Eriana Ning",
            "Siren",
            "plugin-instance");

        Assert.Equal(MarketAcquisitionOrigins.PluginBuilder, request.Origin);
        Assert.Equal("plugin-instance", request.CreatedByPluginInstanceId);
        Assert.StartsWith("plugin-instance:request-builder:", request.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal("Eriana Ning", request.TargetCharacterName);
        Assert.Equal("Siren", request.TargetWorld);
        Assert.Equal("North America", request.Region);
        var line = Assert.Single(request.Lines);
        Assert.Equal((uint)19951, line.ItemId);
        Assert.Equal("AllBelowThreshold", line.QuantityMode);
        Assert.Equal((uint)25, line.MaxQuantity);
        Assert.Equal((uint)276, line.MaxUnitPrice);
    }

    [Fact]
    public void FromRequestView_BuildsEditableDocumentFromRemoteBatch()
    {
        var request = new MarketAcquisitionRequestView
        {
            Id = "batch-1",
            Revision = 7,
            Origin = MarketAcquisitionOrigins.DashboardCreated,
            TargetCharacterName = "Eriana Ning",
            TargetWorld = "Siren",
            Region = "North America",
            WorldMode = "Recommended",
            SweepScope = "Region",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    ItemId = 19951,
                    ItemName = "Koppranickel Ore",
                    QuantityMode = "AllBelowThreshold",
                    MaxQuantity = 25,
                    HqPolicy = "Either",
                    MaxUnitPrice = 276,
                },
            ],
        };

        var document = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);

        Assert.Equal("batch-1", document.RemoteRequestId);
        Assert.Equal(7, document.RemoteRevision);
        Assert.Equal("Eriana Ning", document.TargetCharacterName);
        Assert.Equal("Siren", document.TargetWorld);
        var line = Assert.Single(document.Lines);
        Assert.Equal((uint)19951, line.ItemId);
        Assert.Equal((uint)276, line.MaxUnitPrice);
    }

    [Fact]
    public void MergeClaimWithRequestPreservesSelectedWorldRouting()
    {
        var claim = new MarketAcquisitionClaimView
        {
            Id = "batch-1",
            ClaimToken = "claim-token",
            WorldMode = "Selected",
        };
        var remote = new MarketAcquisitionRequestView
        {
            Id = "batch-1",
            WorldMode = "Selected",
            SelectedWorlds = ["Siren", "Gilgamesh"],
        };

        var merged = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(claim, remote);

        Assert.Equal(["Siren", "Gilgamesh"], merged.SelectedWorlds);
        Assert.Equal("claim-token", merged.ClaimToken);
    }

    private static MarketAcquisitionRequestDocument CreateDocument() => new()
    {
        LocalRequestId = "local-1",
        LocalRevision = 3,
        TargetCharacterName = "Eriana Ning",
        TargetWorld = "Siren",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
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
}
