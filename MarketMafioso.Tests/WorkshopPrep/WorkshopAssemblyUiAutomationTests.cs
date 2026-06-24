using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomationTests
{
    [Fact]
    public void MaterialDeliveryAddonNames_include_company_craft_material()
    {
        Assert.Contains("CompanyCraftMaterial", WorkshopAssemblyUiAutomation.MaterialDeliveryAddonNames);
    }
}
