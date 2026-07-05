using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftAppraisalPanelPresenterTests
{
    [Fact]
    public void Build_WhenWorkshopHostIsAvailable_MakesQuoteLookupPrimaryAndFallbacksAdvanced()
    {
        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = true,
            WorkshopHostQuoteAvailable = true,
            ManualFallbackEnabled = true,
            HasQuoteFilePath = true,
            HasManualCraftCost = true,
        });

        Assert.Equal("Workshop Host: Connected", state.WorkshopHostStatus);
        Assert.Equal("Get Craft Quote", state.PrimaryQuoteActionLabel);
        Assert.True(state.PrimaryQuoteActionEnabled);
        Assert.Equal("Manual / file fallback", state.FallbackSectionLabel);
        Assert.False(state.ShowFallbackControlsByDefault);
        Assert.True(state.ShowManualFallbackControls);
    }

    [Fact]
    public void Build_WhenWorkshopHostIsDisabled_ExplainsFallbackMode()
    {
        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = false,
            WorkshopHostQuoteAvailable = false,
        });

        Assert.Equal("Workshop Host: Off", state.WorkshopHostStatus);
        Assert.Equal("Enable Workshop Host quotes to appraise from the backend.", state.Guidance);
        Assert.False(state.PrimaryQuoteActionEnabled);
        Assert.True(state.ShowFallbackControlsByDefault);
        Assert.False(state.ShowManualFallbackControls);
    }

    [Fact]
    public void Build_WhenManualFallbackIsDisabled_HidesManualFallbackControls()
    {
        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = true,
            WorkshopHostQuoteAvailable = true,
            ManualFallbackEnabled = false,
            HasManualCraftCost = true,
        });

        Assert.False(state.ShowManualFallbackControls);
    }

    [Fact]
    public void Build_WithFetchedQuote_OffersThresholdActions()
    {
        var quote = new CraftAppraisalQuote
        {
            EstimatedUnitCost = 1234m,
            Source = "WorkshopHostCraftArchitect",
            Confidence = "High",
            IsComplete = true,
            AppraisalStatus = "Complete",
            QuotedAtUtc = DateTimeOffset.Parse("2026-07-05T15:20:00+00:00"),
        };

        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = true,
            WorkshopHostQuoteAvailable = true,
            LatestQuote = quote,
            NowUtc = DateTimeOffset.Parse("2026-07-05T15:25:00+00:00"),
        });

        Assert.Equal("Craft quote: 1,234 gil / unit", state.QuoteHeadline);
        Assert.Contains("WorkshopHostCraftArchitect", state.QuoteDetail, StringComparison.Ordinal);
        Assert.True(state.CanApplyQuoteToThreshold);
    }

    [Fact]
    public void Build_WithZeroQuoteAndMissingMaterialEvidence_ExplainsCalculationFailure()
    {
        var quote = new CraftAppraisalQuote
        {
            EstimatedUnitCost = 0m,
            Source = "CraftArchitectLocal",
            Confidence = "Low",
            IsComplete = false,
            AppraisalStatus = "IncompletePriceEvidence",
            Warnings = ["4 active material(s) are missing price evidence."],
            Materials =
            [
                new CraftAppraisalMaterialQuote
                {
                    ItemName = "Growth Formula Kappa",
                    TotalQuantity = 2,
                    UnitCost = 0m,
                    TotalCost = 0m,
                    CostSource = "MissingMarketEvidence",
                    Warnings = ["No usable listing data."],
                },
                new CraftAppraisalMaterialQuote
                {
                    ItemName = "Water Cluster",
                    TotalQuantity = 3,
                    UnitCost = 18m,
                    TotalCost = 54m,
                    CostSource = "Market",
                },
            ],
        };

        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = true,
            WorkshopHostQuoteAvailable = true,
            LatestQuote = quote,
            NowUtc = DateTimeOffset.Parse("2026-07-05T15:25:00+00:00"),
        });

        Assert.False(state.CanApplyQuoteToThreshold);
        Assert.Contains("Quote is incomplete because material price evidence is missing.", state.DiagnosticLines);
        Assert.Contains("Warning: 4 active material(s) are missing price evidence.", state.DiagnosticLines);
        Assert.Contains("Material: Growth Formula Kappa x2, 0 gil/unit, total 0 gil, source MissingMarketEvidence; No usable listing data.", state.DiagnosticLines);
        Assert.Contains("Material: Water Cluster x3, 18 gil/unit, total 54 gil, source Market", state.DiagnosticLines);
    }

    [Fact]
    public void Build_DisablesApplyThresholdForIncompleteNonzeroQuote()
    {
        var quote = new CraftAppraisalQuote
        {
            ItemId = 7017,
            ItemName = "Varnish",
            RequestedQuantity = 999,
            EstimatedUnitCost = 12,
            EstimatedTotalCost = 11988,
            IsComplete = false,
            AppraisalStatus = "IncompletePriceEvidence",
            Materials =
            [
                new CraftAppraisalMaterialQuote
                {
                    ItemId = 3,
                    ItemName = "Partially Priced Material",
                    TotalQuantity = 3,
                    UnitCost = 12,
                    TotalCost = 36,
                    AcquisitionSource = "MarketBuyNq",
                    CostSource = "MarketEvidence"
                },
                new CraftAppraisalMaterialQuote
                {
                    ItemId = 4,
                    ItemName = "Missing Material",
                    TotalQuantity = 1,
                    UnitCost = 0,
                    TotalCost = 0,
                    AcquisitionSource = "MarketBuyNq",
                    CostSource = "MissingEvidence",
                    Warnings = ["Missing Material is missing market price evidence."]
                }
            ]
        };

        var state = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = true,
            WorkshopHostQuoteAvailable = true,
            LatestQuote = quote,
            NowUtc = DateTimeOffset.UnixEpoch
        });

        Assert.False(state.CanApplyQuoteToThreshold);
        Assert.Contains(state.DiagnosticLines, line => line.Contains("IncompletePriceEvidence", StringComparison.OrdinalIgnoreCase));
    }
}
