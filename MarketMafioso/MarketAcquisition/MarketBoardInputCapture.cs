using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardInputCapture
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
}
