using System;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionTargetBases
{
    public const string OnHandTotal = "OnHandTotal";
    public const string RequiredPurchaseQuantity = "RequiredPurchaseQuantity";

    public static string Normalize(string? value, bool exactAcquisition = false) =>
        exactAcquisition || string.Equals(value, RequiredPurchaseQuantity, StringComparison.Ordinal)
            ? RequiredPurchaseQuantity
            : OnHandTotal;
}

public readonly record struct MarketAcquisitionInventoryObservation(
    bool IsAvailable,
    uint Quantity,
    string Message)
{
    public static MarketAcquisitionInventoryObservation Unavailable(string message) => new(false, 0, message);
    public static MarketAcquisitionInventoryObservation Available(uint quantity) =>
        new(true, quantity, $"Observed {quantity:N0} matching item(s) in loaded player inventory.");
}

public interface IMarketAcquisitionInventoryObserver
{
    MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy);
}

public sealed record MarketAcquisitionLinePurchaseBudget
{
    public required string LineId { get; init; }
    public required string TargetBasis { get; init; }
    public required uint TargetQuantity { get; init; }
    public required uint MaximumOverage { get; init; }
    public required uint InitialOnHandQuantity { get; init; }
    public required uint ConfirmedPurchasedQuantity { get; init; }

    public uint ProjectedOnHandQuantity => checked(InitialOnHandQuantity + ConfirmedPurchasedQuantity);
    public uint RemainingQuantity => ProjectedOnHandQuantity >= TargetQuantity
        ? 0
        : TargetQuantity - ProjectedOnHandQuantity;
    public ulong MaximumProjectedQuantity => (ulong)TargetQuantity + MaximumOverage;

    public bool CanAdmit(uint candidateQuantity) =>
        candidateQuantity > 0 &&
        ProjectedOnHandQuantity < TargetQuantity &&
        (ulong)ProjectedOnHandQuantity + candidateQuantity <= MaximumProjectedQuantity;
}
