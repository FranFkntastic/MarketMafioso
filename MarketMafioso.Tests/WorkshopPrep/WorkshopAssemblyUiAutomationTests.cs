using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomationTests
{
    [Fact]
    public void MaterialDeliveryAddonNames_include_company_craft_material()
    {
        Assert.Contains("CompanyCraftMaterial", WorkshopAssemblyUiAutomation.MaterialDeliveryAddonNames);
    }

    [Theory]
    [InlineData("Contribute materials.", true)]
    [InlineData("Contribute materials. ", true)]
    [InlineData("View company crafting log.", false)]
    [InlineData("Complete the construction of the Shark-class Bridge.", false)]
    public void IsContributeMaterialsEntry_only_matches_safe_active_project_open_action(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsContributeMaterialsEntry(text));
    }

    [Theory]
    [InlineData("Contribute 7 darksteel nuggets to the company project?", true)]
    [InlineData("Contribute 4 treated spruce lumber to the company project?", true)]
    [InlineData("You are about to hand over an HQ item. Proceed?", false)]
    [InlineData("Contribute materials. (Quality: 0/100)", false)]
    public void IsContributeItemsPrompt_matches_workshop_contribution_confirmation(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsContributeItemsPrompt(text));
    }

    [Theory]
    [InlineData("You are about to hand over an HQ item. Proceed?", true)]
    [InlineData("Do you really want to trade a high-quality item?", true)]
    [InlineData("Contribute 4 darksteel ingots to the company project?", false)]
    [InlineData("Contribute materials. (Quality: 18/100)", false)]
    public void IsHighQualityHandoffPrompt_matches_hq_warning_variants(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsHighQualityHandoffPrompt(text));
    }

    [Theory]
    [InlineData("Retrieve the shark-class bridge from the company workshop?", true)]
    [InlineData("Retrieve from the company workshop?", true)]
    [InlineData("Retrieve the shark-class bridge?", false)]
    [InlineData("Contribute 7 darksteel nuggets to the company project?", false)]
    public void IsRetrieveFinishedProjectPrompt_matches_product_retrieval_confirmation(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsRetrieveFinishedProjectPrompt(text));
    }

    [Theory]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.ProjectStart), "Craft Shark-class Bridge?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.MaterialContribution), "You are about to hand over an HQ item. Proceed?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.MaterialContribution), "Do you really want to trade a high-quality item?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.MaterialContribution), "Contribute 7 darksteel nuggets to the company project?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.PhaseAdvance), "Advance to the next phase of production?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.FinalConstruction), "Complete the construction of the shark-class bridge?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.ProductRetrieval), "Retrieve the shark-class bridge from the company workshop?", true)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.ProductRetrieval), "Contribute 7 darksteel nuggets to the company project?", false)]
    [InlineData(nameof(WorkshopAssemblyPendingConfirmationKind.None), "Retrieve the shark-class bridge from the company workshop?", false)]
    public void IsPromptAllowedForPendingConfirmation_uses_owned_action_context(
        string kindName,
        string text,
        bool expected)
    {
        var kind = Enum.Parse<WorkshopAssemblyPendingConfirmationKind>(kindName);

        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsPromptAllowedForPendingConfirmation(kind, text));
    }

    [Theory]
    [InlineData("Contribute materials. (Quality: 0/100)", true)]
    [InlineData("Advance to the next phase of production.", true)]
    [InlineData("Complete the construction of the Shark-class Bridge.", true)]
    [InlineData("Collect finished product.", true)]
    [InlineData("View company crafting log.", false)]
    public void IsPostContributionMenuEntry_matches_resume_actions(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsPostContributionMenuEntry(text));
    }

    [Theory]
    [InlineData("Advance to the next phase of production.", true)]
    [InlineData("Complete the construction of the shark-class bridge. (Quality: 72/100)", false)]
    [InlineData("Collect finished product.", false)]
    public void IsAdvancePhaseEntry_matches_only_phase_advance(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsAdvancePhaseEntry(text));
    }

    [Theory]
    [InlineData("Complete the construction of the shark-class bridge. (Quality: 72/100)", true)]
    [InlineData("Advance to the next phase of production.", false)]
    [InlineData("Collect finished product.", false)]
    public void IsCompleteConstructionEntry_matches_only_final_construction(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsCompleteConstructionEntry(text));
    }

    [Theory]
    [InlineData("Collect finished product.", true)]
    [InlineData("Collect finished product. ", true)]
    [InlineData("Complete the construction of the shark-class bridge.", false)]
    public void IsCollectFinishedProductEntry_matches_finished_product_collection(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsCollectFinishedProductEntry(text));
    }
}
