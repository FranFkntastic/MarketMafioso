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
}
