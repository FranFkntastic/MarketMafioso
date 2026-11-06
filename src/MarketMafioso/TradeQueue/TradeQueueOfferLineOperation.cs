using System;
using System.Linq;

namespace MarketMafioso.TradeQueue;

internal enum TradeQueueOfferLineState
{
    ReadyToOffer,
    WaitingForQuantityInput,
    AwaitingSubmitClosure,
    WaitingForSlot,
    Completed,
    Failed,
}

internal sealed record TradeQueueOfferLineResult(
    TradeQueueOfferLineState State,
    string Message)
{
    public bool IsCompleted => State == TradeQueueOfferLineState.Completed;
    public bool IsFailed => State == TradeQueueOfferLineState.Failed;
}

// Owns one exact, already-planned inventory line. The runner sees only its outcome;
// raw slot visibility and the transient numeric dialog never imply completion alone.
internal sealed class TradeQueueOfferLineOperation
{
    private readonly ITradeQueueIo io;
    private readonly TradeQueueBatchLine line;
    private readonly int expectedQuantityAfterOffer;
    private readonly DateTimeOffset deadline;
    private TradeQueueOfferLineState state = TradeQueueOfferLineState.ReadyToOffer;

    public TradeQueueOfferLineOperation(
        ITradeQueueIo io,
        TradeQueueBatchLine line,
        TradeOfferObservation beforeOffer,
        DateTimeOffset deadline)
    {
        this.io = io;
        this.line = line;
        expectedQuantityAfterOffer = checked(QuantityIn(beforeOffer) + line.Quantity);
        this.deadline = deadline;
    }

    public bool HasDispatched => state != TradeQueueOfferLineState.ReadyToOffer;

    private int QuantityIn(TradeOfferObservation offer) => offer.Items
        .Where(item => item.ItemId == line.ItemId && item.IsHighQuality == line.IsHighQuality)
        .Sum(item => item.Quantity);

    public TradeQueueOfferLineResult Advance(DateTimeOffset now)
    {
        if (state is TradeQueueOfferLineState.Completed or TradeQueueOfferLineState.Failed)
            return Result();
        if (now > deadline)
            return Fail($"Timed out while offering {line.ItemName}.");

        if (state == TradeQueueOfferLineState.ReadyToOffer)
        {
            if (!io.TryOfferItem(line, out var offerError))
                return string.IsNullOrWhiteSpace(offerError)
                    ? Result()
                    : Fail(offerError);

            state = line.SourceStackQuantity > 1
                ? TradeQueueOfferLineState.WaitingForQuantityInput
                : TradeQueueOfferLineState.WaitingForSlot;
            return Result();
        }

        if (state == TradeQueueOfferLineState.WaitingForQuantityInput)
        {
            // FFXIV can populate the trade slot before the stack dialog is submitted.
            // That observation is deliberately not completion evidence for this line.
            if (!io.IsNumericInputOpen)
                return Result();
            if (!io.TrySubmitQuantity(line.Quantity, out var quantityError))
                return string.IsNullOrWhiteSpace(quantityError)
                    ? Result()
                    : Fail(quantityError);

            state = TradeQueueOfferLineState.AwaitingSubmitClosure;
            return Result();
        }

        if (state == TradeQueueOfferLineState.AwaitingSubmitClosure)
        {
            if (io.IsNumericInputOpen)
                return Result();
            state = TradeQueueOfferLineState.WaitingForSlot;
        }

        if (state == TradeQueueOfferLineState.WaitingForSlot)
        {
            var offer = io.ObserveOffer();
            if (offer is { SyncPending: false } && QuantityIn(offer) >= expectedQuantityAfterOffer)
                state = TradeQueueOfferLineState.Completed;
        }

        return Result();
    }

    private TradeQueueOfferLineResult Fail(string message)
    {
        state = TradeQueueOfferLineState.Failed;
        return new(state, message);
    }

    private TradeQueueOfferLineResult Result() =>
        new(state, state switch
        {
            TradeQueueOfferLineState.ReadyToOffer => $"Ready to offer {line.ItemName}.",
            TradeQueueOfferLineState.WaitingForQuantityInput => $"Waiting for the quantity input for {line.ItemName}.",
            TradeQueueOfferLineState.AwaitingSubmitClosure => $"Waiting for the quantity input for {line.ItemName} to close.",
            TradeQueueOfferLineState.WaitingForSlot => $"Waiting for {line.ItemName} to occupy its trade slot.",
            TradeQueueOfferLineState.Completed => $"Offered {line.ItemName}.",
            _ => $"Offering {line.ItemName} failed.",
        });
}
