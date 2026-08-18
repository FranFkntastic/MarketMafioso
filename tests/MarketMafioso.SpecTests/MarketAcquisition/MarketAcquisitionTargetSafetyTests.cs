using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionTargetSafetyTests
{
    [Theory]
    [InlineData("line:gold", "Siren", "line:gold", "Siren", false)]
    [InlineData("line:gold", "Siren", "line:gold", "Faerie", true)]
    [InlineData("line:gold", "Siren", "line:silver", "Siren", true)]
    public void Accumulator_IsScopedToBothLineAndWorld(
        string activeLine,
        string activeWorld,
        string nextLine,
        string nextWorld,
        bool expectedReset)
    {
        Assert.Equal(expectedReset, MarketAcquisitionRouteEngine.ShouldResetLinePurchaseAccumulator(
            activeLine,
            activeWorld,
            nextLine,
            nextWorld));
    }

    [Fact]
    public void Budget_AdmitsAtMostOneCrossingListingWithinConfiguredOverage()
    {
        var budget = new MarketAcquisitionLinePurchaseBudget
        {
            LineId = "line:gold",
            TargetBasis = MarketAcquisitionTargetBases.OnHandTotal,
            TargetQuantity = 1_000,
            MaximumOverage = 25,
            InitialOnHandQuantity = 960,
            ConfirmedPurchasedQuantity = 0,
        };

        Assert.True(budget.CanAdmit(60));
        Assert.False((budget with { ConfirmedPurchasedQuantity = 60 }).CanAdmit(1));
        Assert.False((budget with { MaximumOverage = 0 }).CanAdmit(60));
    }

    [Fact]
    public void LivePlanner_AfterCrossingTargetWithinAllowance_RejectsEveryLaterListing()
    {
        var request = CreateRequest(maximumOverage: 25);
        var subtask = CreateSubtask(maximumOverage: 25);
        var plan = CreatePlan(subtask);
        var listings = new[]
        {
            Listing("first", quantity: 60, unitPrice: 10),
            Listing("second", quantity: 1, unitPrice: 11),
        };

        var result = MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            subtask,
            "Siren",
            itemId: 3_627,
            listings,
            alreadyPurchasedQuantity: 960);

        Assert.Equal("WouldBuy", result.Rows[0].Decision);
        Assert.Equal("TargetSatisfied", result.Rows[1].Reason);
        Assert.Equal(60u, result.WouldBuyQuantity);
    }

    [Fact]
    public void LivePlanner_ReportsOverageLimitWhenNoWholeListingFits()
    {
        var request = CreateRequest(maximumOverage: 0);
        var subtask = CreateSubtask(maximumOverage: 0);

        var result = MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            CreatePlan(subtask),
            subtask,
            "Siren",
            itemId: 3_627,
            [Listing("too-large", quantity: 60, unitPrice: 10)],
            alreadyPurchasedQuantity: 960);

        Assert.Equal(MarketAcquisitionLiveCandidateStatuses.OverageLimit, result.Status);
        Assert.Equal(0u, result.WouldBuyQuantity);
    }

    [Theory]
    [InlineData("Purchase 58 Gold Ore for 5,800 gil?", 58, "Gold Ore", true)]
    [InlineData("Purchase 99 Gold Ore for 9,900 gil?", 58, "Gold Ore", false)]
    [InlineData("Purchase 58 Silver Ore for 5,800 gil?", 58, "Gold Ore", false)]
    public void ConfirmationPrompt_MustMatchExactQuantityAndItem(
        string prompt,
        uint quantity,
        string itemName,
        bool expected)
    {
        Assert.Equal(expected, MarketBoardPurchasePromptPolicy.Validate(prompt, quantity, itemName).IsValid);
    }

    private static MarketAcquisitionRequestView CreateRequest(uint maximumOverage) => new()
    {
        Id = "request:gold",
        Status = "AcceptedInPlugin",
        TargetCharacterName = "Wei Ning",
        TargetWorld = "Siren",
        Region = "North-America",
        ItemId = 3_627,
        ItemName = "Gold Ore",
        QuantityMode = "TargetQuantity",
        Quantity = 1_000,
        TargetBasis = MarketAcquisitionTargetBases.OnHandTotal,
        MaximumOverage = maximumOverage,
        HqPolicy = "Either",
        MaxUnitPrice = 100,
        MaxTotalGil = 1_000_000,
        WorldMode = "Recommended",
    };

    private static MarketAcquisitionWorldItemSubtask CreateSubtask(uint maximumOverage) => new()
    {
        LineId = "line:gold",
        ItemId = 3_627,
        ItemName = "Gold Ore",
        WorldName = "Siren",
        DataCenter = "Aether",
        QuantityMode = "TargetQuantity",
        RequestedQuantity = 1_000,
        TargetBasis = MarketAcquisitionTargetBases.OnHandTotal,
        MaximumOverage = maximumOverage,
        HqPolicy = "Either",
        MaxUnitPrice = 100,
        GilCap = 1_000_000,
    };

    private static MarketAcquisitionPlan CreatePlan(MarketAcquisitionWorldItemSubtask subtask) => new()
    {
        RequestId = "request:gold",
        Status = "Ready",
        ItemId = subtask.ItemId,
        RequestedQuantity = subtask.RequestedQuantity,
        Lines =
        [
            new MarketAcquisitionPlanLine
            {
                LineId = subtask.LineId,
                ItemId = subtask.ItemId,
                ItemName = subtask.ItemName,
                QuantityMode = subtask.QuantityMode,
                RequestedQuantity = subtask.RequestedQuantity,
                TargetBasis = subtask.TargetBasis,
                MaximumOverage = subtask.MaximumOverage,
                HqPolicy = subtask.HqPolicy,
                MaxUnitPrice = subtask.MaxUnitPrice,
                GilCap = subtask.GilCap,
            },
        ],
        WorldBatches =
        [
            new MarketAcquisitionWorldBatch
            {
                WorldName = subtask.WorldName,
                DataCenter = subtask.DataCenter,
                ItemSubtasks = [subtask],
            },
        ],
    };

    private static MarketBoardLiveListing Listing(string id, uint quantity, uint unitPrice) => new()
    {
        ItemId = 3_627,
        WorldName = "Siren",
        ListingId = id,
        RetainerId = $"retainer:{id}",
        RetainerName = id,
        Quantity = quantity,
        UnitPrice = unitPrice,
    };
}
