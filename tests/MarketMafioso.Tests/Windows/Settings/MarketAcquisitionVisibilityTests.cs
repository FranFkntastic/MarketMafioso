using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.Windows.Main.Settings;

namespace MarketMafioso.Tests.Windows.Settings;

public sealed class MarketAcquisitionVisibilityTests
{
    [Fact]
    public void LockedConfiguration_DoesNotAdvertiseMarketAcquisitionInSettingsNavigation()
    {
        var config = new Configuration();
        var acquisitionCatalog = new SettingsNavigationCatalog(new MarketAcquisitionSettingsPages(config).Descriptors);
        var privateFeatures = new AdvancedSettingsPages(config, () => { }).Descriptors
            .Single(page => page.Id == "advanced.private-features");

        Assert.Empty(acquisitionCatalog.GetVisiblePages());
        Assert.False(privateFeatures.Matches("Market Acquisition"));
        Assert.DoesNotContain(config.SettingsExpandedFolderPaths, path => path.Contains("Market Acquisition", StringComparison.Ordinal));
    }

    [Fact]
    public void UnlockedConfiguration_ExposesMarketAcquisitionSettingsPages()
    {
        var config = new Configuration { EnableMarketAcquisition = true };
        var catalog = new SettingsNavigationCatalog(new MarketAcquisitionSettingsPages(config).Descriptors);

        var visible = catalog.GetVisiblePages();

        Assert.Equal(2, visible.Count);
        Assert.All(visible, page => Assert.Contains("Market Acquisition", page.Path, StringComparison.Ordinal));
    }
}
