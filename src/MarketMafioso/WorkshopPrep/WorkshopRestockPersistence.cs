using System;

namespace MarketMafioso.WorkshopPrep;

/// <summary>Framework-thread checkpoint queue; a storage failure never reverses observed game state.</summary>
public sealed class WorkshopRestockPersistence(Action save, Action<Exception> log)
{
    private bool pending;
    private DateTime retryAtUtc;
    public string? Error { get; private set; }
    public void Request() => pending = true;

    public void Flush()
    {
        if (!pending || DateTime.UtcNow < retryAtUtc) return;
        pending = false;
        try
        {
            save();
            Error = null;
            retryAtUtc = default;
        }
        catch (Exception ex)
        {
            Error = $"Restock state is current in memory, but its checkpoint could not be saved: {ex.Message}";
            pending = true;
            retryAtUtc = DateTime.UtcNow.AddSeconds(5);
            log(ex);
        }
    }
}
