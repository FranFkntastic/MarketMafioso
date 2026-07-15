namespace MarketMafioso.Tests;

public sealed class WorkshopHostApiKeyRoutingTests
{
    [Fact]
    public void ResolveAcquisitionKey_PrefersDedicatedAcquisitionKey()
    {
        var config = new Configuration
        {
            ApiKey = "mmf_client_full",
            CommandPickupApiKey = "mmf_ca_acquisition",
        };

        Assert.Equal("mmf_ca_acquisition", WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config));
    }

    [Fact]
    public void ResolveAcquisitionKey_FallsBackToMarketMafiosoClientKey()
    {
        var config = new Configuration
        {
            ApiKey = "mmf_client_full",
            CommandPickupApiKey = string.Empty,
        };

        Assert.Equal("mmf_client_full", WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config));
    }

    [Theory]
    [InlineData("mmf_ca_secret")]
    [InlineData("  MMF_CA_secret")]
    public void IsCraftArchitectKey_RecognizesManagedPrefix(string value)
    {
        Assert.True(WorkshopHostApiKeyRouting.IsCraftArchitectKey(value));
        Assert.False(WorkshopHostApiKeyRouting.IsMarketMafiosoClientKey(value));
    }

    [Fact]
    public void NormalizeConfiguredKeys_MovesMisplacedCraftArchitectKey()
    {
        var config = new Configuration { ApiKey = "mmf_ca_acquisition" };

        var changed = WorkshopHostApiKeyRouting.NormalizeConfiguredKeys(config);

        Assert.True(changed);
        Assert.Empty(config.ApiKey);
        Assert.Equal("mmf_ca_acquisition", config.CommandPickupApiKey);
    }

    [Fact]
    public void NormalizeConfiguredKeys_PreservesLegacyFullClientKeyCompatibility()
    {
        var config = new Configuration { CommandPickupApiKey = "legacy-full-client-key" };

        var changed = WorkshopHostApiKeyRouting.NormalizeConfiguredKeys(config);

        Assert.True(changed);
        Assert.Equal("legacy-full-client-key", config.ApiKey);
        Assert.Equal("legacy-full-client-key", config.CommandPickupApiKey);
    }
}
