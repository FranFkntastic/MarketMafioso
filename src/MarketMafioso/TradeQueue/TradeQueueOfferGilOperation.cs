namespace MarketMafioso.TradeQueue;

internal enum TradeQueueOfferGilState
{
    ReadyToOpenInput,
    WaitingForInput,
    Completed,
    Failed,
}

internal sealed record TradeQueueOfferGilResult(
    TradeQueueOfferGilState State,
    string Message,
    bool ActionIssued = false)
{
    public bool IsCompleted => State == TradeQueueOfferGilState.Completed;
    public bool IsFailed => State == TradeQueueOfferGilState.Failed;
}

// Owns the currency-input protocol for one already-planned batch amount. The runner
// supplies pacing and the batch deadline; this operation never treats opening the input as payment.
internal sealed class TradeQueueOfferGilOperation
{
    private readonly ITradeQueueIo io;
    private readonly int amount;
    private TradeQueueOfferGilState state = TradeQueueOfferGilState.ReadyToOpenInput;

    public TradeQueueOfferGilOperation(ITradeQueueIo io, int amount)
    {
        this.io = io;
        this.amount = amount;
    }

    public bool IsCompleted => state == TradeQueueOfferGilState.Completed;

    public TradeQueueOfferGilResult Advance()
    {
        if (state is TradeQueueOfferGilState.Completed or TradeQueueOfferGilState.Failed)
            return Result();
        if (amount <= 0)
            return Fail("Trade gil amount must be positive.");

        if (state == TradeQueueOfferGilState.ReadyToOpenInput)
        {
            if (!io.TryOpenGilInput(out var openError))
                return string.IsNullOrWhiteSpace(openError) ? Result() : Fail(openError);

            state = TradeQueueOfferGilState.WaitingForInput;
            return new(state, "Waiting for the trade gil input.", ActionIssued: true);
        }

        if (!io.IsNumericInputOpen)
            return Result();
        if (!io.TrySubmitQuantity(amount, out var submitError))
            return string.IsNullOrWhiteSpace(submitError) ? Result() : Fail(submitError);

        state = TradeQueueOfferGilState.Completed;
        return Result();
    }

    private TradeQueueOfferGilResult Fail(string message)
    {
        state = TradeQueueOfferGilState.Failed;
        return new(state, message);
    }

    private TradeQueueOfferGilResult Result() => new(state, state switch
    {
        TradeQueueOfferGilState.ReadyToOpenInput => "Ready to open the trade gil input.",
        TradeQueueOfferGilState.WaitingForInput => "Waiting for the trade gil input.",
        TradeQueueOfferGilState.Completed => $"Offered {amount:N0} gil.",
        _ => "Offering trade gil failed.",
    });
}
