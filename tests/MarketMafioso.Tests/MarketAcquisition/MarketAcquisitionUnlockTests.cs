namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionUnlockTests
{
    [Fact]
    public void IsUnlocked_DefaultsToFalse()
    {
        var config = new MarketMafioso.Configuration();

        Assert.False(MarketMafioso.MarketAcquisition.MarketAcquisitionUnlock.IsUnlocked(config));
    }

    [Fact]
    public void TryUnlock_RejectsWrongKeyWithoutChangingConfig()
    {
        var config = new MarketMafioso.Configuration();

        var unlocked = MarketMafioso.MarketAcquisition.MarketAcquisitionUnlock.TryUnlock(
            config,
            "wrong-key",
            "a98f35b86e4b17251228a34c57eb316b9d30b67de32b54953a8751b4a3de9961",
            new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.False(unlocked);
        Assert.False(config.EnableMarketAcquisition);
        Assert.Null(config.MarketAcquisitionUnlockedAtUtc);
    }

    [Fact]
    public void TryUnlock_AcceptsMatchingKey()
    {
        var config = new MarketMafioso.Configuration();
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var unlocked = MarketMafioso.MarketAcquisition.MarketAcquisitionUnlock.TryUnlock(
            config,
            "test-key",
            "62af8704764faf8ea82fc61ce9c4c3908b6cb97d463a634e9e587d7c885db0ef",
            now);

        Assert.True(unlocked);
        Assert.True(config.EnableMarketAcquisition);
        Assert.Equal(now, config.MarketAcquisitionUnlockedAtUtc);
    }

    [Fact]
    public void Lock_DisablesMarketAcquisitionWithoutClearingOtherState()
    {
        var config = new MarketMafioso.Configuration
        {
            EnableMarketAcquisition = true,
            MarketAcquisitionUnlockedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            ActiveMarketAcquisitionClaim = new MarketMafioso.PersistedMarketAcquisitionClaim
            {
                Id = "claim-1",
            },
        };

        MarketMafioso.MarketAcquisition.MarketAcquisitionUnlock.Lock(config);

        Assert.False(config.EnableMarketAcquisition);
        Assert.Null(config.MarketAcquisitionUnlockedAtUtc);
        Assert.NotNull(config.ActiveMarketAcquisitionClaim);
    }
}
