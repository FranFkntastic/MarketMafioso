namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed class MarketListingPresentationSession
{
    public bool IsActive { get; private set; }

    public void ObserveSnapshot() => IsActive = true;

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
