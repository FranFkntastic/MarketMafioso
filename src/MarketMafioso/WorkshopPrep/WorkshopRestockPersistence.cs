using System;

namespace MarketMafioso.WorkshopPrep;

/// <summary>Framework-thread checkpoint queue; a storage failure never reverses observed game state.</summary>
public sealed class WorkshopRestockPersistence(Action save, Action<Exception> log)
{
    private bool pending;
    public string? Error { get; private set; }
    public void Request() => pending = true;

    public void Flush()
    {
        if (!pending) return;
        pending = false;
        try
        {
            save();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Restock state is current in memory, but its checkpoint could not be saved: {ex.Message}";
            log(ex);
        }
    }
}
