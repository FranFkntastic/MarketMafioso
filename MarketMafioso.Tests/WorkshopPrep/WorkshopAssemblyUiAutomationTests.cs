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
    [InlineData("Contribute materials. (Quality: 0/100)", true)]
    [InlineData("Advance to the next phase of production.", true)]
    [InlineData("Complete the construction of the Shark-class Bridge.", true)]
    [InlineData("Collect finished product.", true)]
    [InlineData("View company crafting log.", false)]
    public void IsPostContributionMenuEntry_matches_resume_actions(string text, bool expected)
    {
        Assert.Equal(expected, WorkshopAssemblyUiAutomation.IsPostContributionMenuEntry(text));
    }
}
