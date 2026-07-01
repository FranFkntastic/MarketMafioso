namespace MarketMafioso.MarketAcquisition;

public static class MarketBoardPaginationProbe
{
    public static MarketBoardPaginationProbeResult Evaluate(MarketBoardPaginationState state)
    {
        if (!state.IsTruncated)
        {
            return new MarketBoardPaginationProbeResult
            {
                Status = "NotTruncated",
                Message = "The visible market-board listing cache is not truncated.",
                CanAttemptLiveProbe = false,
                Before = state,
            };
        }

        if (!state.HasCoherentRequestIds)
        {
            return new MarketBoardPaginationProbeResult
            {
                Status = "RequestIdsNotCoherent",
                Message = "The visible cache is truncated, but current and next request ids do not indicate a safe page request boundary.",
                CanAttemptLiveProbe = false,
                Before = state,
            };
        }

        return new MarketBoardPaginationProbeResult
        {
            Status = "ReadyForLiveProbe",
            Message = "The visible cache is truncated and request ids look coherent enough for a diagnostics-only live probe.",
            CanAttemptLiveProbe = true,
            Before = state,
        };
    }

    public static MarketBoardPaginationProbeResult EvaluateContinuation(
        MarketBoardPaginationState before,
        MarketBoardPaginationState after)
    {
        if (!after.IsContinuationOf(before))
        {
            return new MarketBoardPaginationProbeResult
            {
                Status = "WrongContinuation",
                Message = "The post-probe market-board state changed item or world.",
                CanAttemptLiveProbe = false,
                Before = before,
                After = after,
            };
        }

        if (after.CurrentRequestId == before.CurrentRequestId)
        {
            return new MarketBoardPaginationProbeResult
            {
                Status = "Unchanged",
                Message = "The post-probe market-board request id did not advance.",
                CanAttemptLiveProbe = false,
                Before = before,
                After = after,
            };
        }

        return new MarketBoardPaginationProbeResult
        {
            Status = "Advanced",
            Message = "The post-probe market-board state stayed on the same item/world and advanced request id.",
            CanAttemptLiveProbe = false,
            Before = before,
            After = after,
        };
    }
}

public sealed record MarketBoardPaginationProbeResult
{
    public string Status { get; init; } = "Unavailable";
    public string Message { get; init; } = string.Empty;
    public bool CanAttemptLiveProbe { get; init; }
    public MarketBoardPaginationState? Before { get; init; }
    public MarketBoardPaginationState? After { get; init; }
}
