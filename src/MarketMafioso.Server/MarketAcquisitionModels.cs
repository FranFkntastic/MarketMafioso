namespace MarketMafioso.Server;

public sealed record MarketAcquisitionCreateResult(MarketAcquisitionRequestView Request, bool IsReplay);

public sealed class MarketAcquisitionIdempotencyConflictException : Exception
{
    public MarketAcquisitionIdempotencyConflictException()
        : base("Idempotency key was already used with a different request body.")
    {
    }
}

public sealed class MarketAcquisitionAttemptSequenceConflictException : Exception
{
    public MarketAcquisitionAttemptSequenceConflictException()
        : base("Attempt event sequence was already used with a different request body.")
    {
    }
}

public sealed class MarketAcquisitionInvalidTransitionException : Exception
{
    public MarketAcquisitionInvalidTransitionException(string status, string targetStatus)
        : base($"Cannot move acquisition request from {status} to {targetStatus}.")
    {
    }
}

public sealed class MarketAcquisitionClaimReleasedException : Exception
{
    public const string Code = "claim_released";

    public MarketAcquisitionClaimReleasedException(string requestId, string status)
        : base($"Acquisition request {requestId} no longer holds a live claim (status {status}).")
    {
        RequestId = requestId;
        Status = status;
    }

    public string RequestId { get; }
    public string Status { get; }
}

public sealed class MarketAcquisitionRevisionConflictException : Exception
{
    public MarketAcquisitionRevisionConflictException(int expectedRevision, int actualRevision)
        : base($"Acquisition request revision changed from {expectedRevision} to {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public int ExpectedRevision { get; }
    public int ActualRevision { get; }
}

public sealed class MarketAcquisitionInvalidLineException : Exception
{
    public MarketAcquisitionInvalidLineException(string requestId, string lineId)
        : base($"Line {lineId} does not belong to acquisition request {requestId}.")
    {
    }
}
