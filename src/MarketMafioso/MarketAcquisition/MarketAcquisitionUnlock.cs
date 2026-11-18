namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionUnlock
{
    public static bool IsUnlocked(Configuration config) => config.EnableMarketAcquisition;

    public static void Lock(Configuration config)
    {
        config.EnableMarketAcquisition = false;
        config.MarketAcquisitionLockedByUser = true;
        config.MarketAcquisitionUnlockedAtUtc = null;
    }
}
