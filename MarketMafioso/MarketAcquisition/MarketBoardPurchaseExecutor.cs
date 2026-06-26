using System;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketBoardPurchaseAdapter
{
    MarketBoardPurchaseResult ExecutePurchase(
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing);
}

public sealed class MarketBoardPurchaseExecutor
{
    private readonly IMarketBoardPurchaseAdapter adapter;

    public MarketBoardPurchaseExecutor(IMarketBoardPurchaseAdapter adapter)
    {
        this.adapter = adapter;
    }

    public MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveDryRun dryRun,
        MarketBoardReadResult freshRead)
    {
        ArgumentNullException.ThrowIfNull(dryRun);
        ArgumentNullException.ThrowIfNull(freshRead);

        var candidate = MarketBoardPurchasePlanner.SelectFirstCandidate(dryRun);
        if (candidate == null)
        {
            return new MarketBoardPurchaseResult
            {
                Status = "NoCandidate",
                Message = "No safe live purchase candidate is available.",
            };
        }

        var revalidation = MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);
        if (!revalidation.CanAttemptPurchase)
        {
            return new MarketBoardPurchaseResult
            {
                Status = revalidation.Status,
                Message = revalidation.Message,
                Candidate = candidate,
            };
        }

        return adapter.ExecutePurchase(candidate, revalidation.FreshListing!);
    }
}
