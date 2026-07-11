using Newtonsoft.Json;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireConfigurationTests
{
    [Fact]
    public void PlayerSignedGearProtection_DefaultsOff()
    {
        var config = new SquireConfiguration();

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ShowNonEquipment);
    }

    [Fact]
    public void LegacyImplicitSignedGearDefault_DoesNotBecomeAnOptIn()
    {
        var config = JsonConvert.DeserializeObject<SquireConfiguration>("{\"ProtectSignedGear\":true}")!;

        Assert.False(config.ProtectPlayerSignedGear);
    }
}
