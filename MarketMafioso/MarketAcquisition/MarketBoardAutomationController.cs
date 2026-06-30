using System;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardAutomationController : IDisposable
{
    public MarketBoardPurchaseSession? PurchaseSession { get; private set; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; private set; }

    public bool IsBusy => PurchaseSession?.IsActive == true;

    public string Status =>
        PurchaseSession?.Status ??
        LastPurchaseResult?.Status ??
        "Idle";

    public string Message =>
        PurchaseSession?.Message ??
        LastPurchaseResult?.Message ??
        "No market-board automation is active.";

    public void RecordPurchaseSelection(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan confirmationWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        LastPurchaseResult = result;
        PurchaseSession = result.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) &&
                          result.Candidate != null
            ? MarketBoardPurchaseSession.Start(result.Candidate, nowUtc, confirmationWatchdog)
            : null;
    }

    public void RecordConfirmationAttempt(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan listingRemovalWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        LastPurchaseResult = result;
        if (PurchaseSession != null)
            PurchaseSession = PurchaseSession.RecordConfirmationAttempt(result, nowUtc, listingRemovalWatchdog);
    }

    public void RecordFreshRead(MarketBoardReadResult readResult, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        if (PurchaseSession != null)
            PurchaseSession = PurchaseSession.RecordFreshRead(readResult, nowUtc);
    }

    public void Abort(string message)
    {
        PurchaseSession = null;
        LastPurchaseResult = new MarketBoardPurchaseResult
        {
            Status = "Aborted",
            Message = message,
        };
    }

    public void Clear()
    {
        PurchaseSession = null;
        LastPurchaseResult = null;
    }

    public void Dispose()
    {
        Clear();
    }
}
