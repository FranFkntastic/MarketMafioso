using Newtonsoft.Json;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireConfigurationTests
{
    [Fact]
    public void PlayerSignedGearProtection_DefaultsOff()
    {
        Assert.False(new SquireConfiguration().ProtectPlayerSignedGear);
    }

    [Fact]
    public void LegacyImplicitSignedGearDefault_DoesNotBecomeAnOptIn()
    {
        var config = JsonConvert.DeserializeObject<SquireConfiguration>("{\"ProtectSignedGear\":true}")!;

        Assert.False(config.ProtectPlayerSignedGear);
    }
}
