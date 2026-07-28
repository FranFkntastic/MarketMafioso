using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopVendorRestockPresentationTests
{
    [Theory]
    [InlineData(PresentationScenario.CompletedReceipt)]
    [InlineData(PresentationScenario.NoDeadAction)]
    [InlineData(PresentationScenario.ExactAuthority)]
    public void Presentation_contract(PresentationScenario scenario)
    {
        switch (scenario)
        {
            case PresentationScenario.CompletedReceipt:
                Completed_vendor_run_leads_with_the_verified_receipt();
                break;
            case PresentationScenario.NoDeadAction:
                Missing_materials_without_automatic_coverage_do_not_create_a_dead_action();
                break;
            case PresentationScenario.ExactAuthority:
                Executable_review_names_the_exact_authority();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void Completed_vendor_run_leads_with_the_verified_receipt()
    {
        var review = Review(Material(1, "Mythril Rivets", vendorNeed: 0));
        var run = new PersistedWorkshopVendorRestockRun
        {
            Phase = WorkshopVendorRestockPhase.Completed,
            Lines =
            [
                new()
                {
                    ItemId = 1,
                    ItemName = "Mythril Rivets",
                    Offer = new()
                    {
                        NpcName = "Amalj'aa vendor",
                    },
                },
            ],
            Receipts =
            [
                new()
                {
                    ItemId = 1,
                    Quantity = 30,
                    SpentGil = 17_490,
                },
                new()
                {
                    ItemId = 1,
                    Quantity = 24,
                    SpentGil = 7_512,
                },
            ],
        };

        Assert.Equal(
            "Bought 54 items for 25,002 gil from Amalj'aa vendor.",
            WorkshopVendorRestockPresentation.Describe(run, review));
        Assert.Equal(
            "Mythril Rivets ×54 · 25,002 gil",
            WorkshopVendorRestockPresentation.DescribeReceiptDetails(run));
    }

    private static void Missing_materials_without_automatic_coverage_do_not_create_a_dead_action()
    {
        var review = Review(
            Material(1, "Ancient Lumber", vendorNeed: 9, automaticCoverage: false),
            Material(2, "Cobalt Ingot", vendorNeed: 78, automaticCoverage: false));

        Assert.Null(WorkshopVendorRestockPresentation.BuildStartActionLabel(review, true));
        Assert.Equal(
            "2 materials · 87 units still need another source",
            WorkshopVendorRestockPresentation.DescribeRemaining(review));
    }

    private static void Executable_review_names_the_exact_authority()
    {
        var material = Material(1, "Steel Ingot", vendorNeed: 54) with
        {
            RetainerPlannedQuantity = 12,
            ApprovedVendorQuantity = 54,
        };
        var review = Review(material);

        Assert.Equal(
            "Retrieve 12 + buy 54 · up to 540 gil",
            WorkshopVendorRestockPresentation.BuildStartActionLabel(review, true));
    }

    private static WorkshopVendorRestockReview Review(
        params WorkshopMaterialProcurement[] materials) =>
        new("QUEUE", materials, []);

    private static WorkshopMaterialProcurement Material(
        uint itemId,
        string name,
        int vendorNeed,
        bool automaticCoverage = true)
    {
        var availability = new WorkshopMaterialAvailability(
            itemId,
            name,
            1,
            vendorNeed,
            0,
            0,
            vendorNeed,
            vendorNeed,
            []);
        var candidate = vendorNeed == 0 || !automaticCoverage
            ? null
            : new WorkshopVendorCandidate(
                new(
                    itemId,
                    name,
                    1,
                    10,
                    50,
                    0,
                    100,
                    "Vendor",
                    129,
                    default,
                    [2]),
                new(
                    Franthropy.Dalamud.Automation.Vendors.GilVendorAccessState.Verified,
                    "test",
                    "Verified."));
        return new(
            availability,
            0,
            vendorNeed,
            candidate is null ? [] : [candidate],
            candidate,
            candidate is not null,
            vendorNeed);
    }

    public enum PresentationScenario
    {
        CompletedReceipt,
        NoDeadAction,
        ExactAuthority,
    }
}
