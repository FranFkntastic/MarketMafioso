namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed class MarketListingPresentationSession
{
    private uint? territoryId;

    public bool IsActive { get; private set; }

    public void ObserveSnapshot(uint currentTerritoryId)
    {
        territoryId = currentTerritoryId;
        IsActive = true;
    }

    public void ObserveNativeState(
        bool clientAvailable,
        uint currentTerritoryId,
        bool resultVisible,
        bool resultMatchesSnapshot,
        bool searchVisible,
        bool agentActive,
        bool recoveryActive)
    {
        if (!clientAvailable || territoryId != currentTerritoryId)
        {
            Close();
            return;
        }

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

    public void Close()
    {
        territoryId = null;
        IsActive = false;
    }
}
