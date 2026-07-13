using Newtonsoft.Json;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireConfigurationTests
{
    [Fact]
    public void PlayerSignedGearProtection_DefaultsOff()
    {
        var config = new SquireConfiguration();

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ProtectFutureLevelingGearOptIn);
        Assert.False(config.ShowNonEquipment);
        Assert.True(config.ProtectBlueAndPurpleGear);
        Assert.False(config.AllowRiskyMateriaRetrieval);
        Assert.True(config.RecoverFromKnockout);
        Assert.True(config.WaitForCombatToEnd);
        Assert.Equal(90, config.CombatRecoveryTimeoutSeconds);
        Assert.False(config.LeaveDutyToExecute);
        Assert.True(config.PauseGatherBuddyReborn);
        Assert.True(config.PauseQuestionable);
        Assert.True(config.PauseArtisan);
        Assert.True(config.CloseSafeUserMenus);
    }

    [Fact]
    public void LegacyImplicitSignedGearDefault_DoesNotBecomeAnOptIn()
    {
        var config = JsonConvert.DeserializeObject<SquireConfiguration>(
            "{\"ProtectSignedGear\":true,\"ProtectFutureLevelingGear\":true}")!;

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ProtectFutureLevelingGearOptIn);
    }
}
