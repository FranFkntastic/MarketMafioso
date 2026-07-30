namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed class RemoteMarketOverlaySession
{
    public bool IsActive { get; private set; }

    public void ObserveSnapshot() => IsActive = true;

    public void BeginRecovery() => IsActive = true;

    public void ObserveNativeState(
        bool resultVisible,
        bool resultMatchesSnapshot,
        bool searchVisible,
        bool agentActive,
        bool recoveryActive)
    {
        if (recoveryActive)
        {
            IsActive = true;
            return;
        }

        if (resultVisible)
        {
            IsActive = resultMatchesSnapshot;
            return;
        }

        if (searchVisible || !agentActive)
            IsActive = false;
    }

    public void Close() => IsActive = false;
}
