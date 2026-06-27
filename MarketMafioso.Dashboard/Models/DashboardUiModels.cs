namespace MarketMafioso.Dashboard.Models;

public sealed record DashboardNotice
{
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record QueuedAcquisitionItem
{
    public XivItemSearchResult Item { get; init; } = new();
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = "Recommended";

    public string QuantityDisplay => QuantityMode == "AllBelowThreshold"
        ? Quantity == 0 ? "All safe stock" : $"Max {Quantity:N0}"
        : Quantity.ToString("N0");

    public string GilCapDisplay => MaxTotalGil == 0
        ? "No cap"
        : MaxTotalGil.ToString("N0");
}

public sealed record RequestActionResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;

    public static RequestActionResult Ok(string message) => new()
    {
        Succeeded = true,
        Message = message,
    };

    public static RequestActionResult Fail(string message) => new()
    {
        Succeeded = false,
        Message = message,
    };
}
