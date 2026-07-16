namespace MarketMafioso.Dashboard.Models;

public static class MarketAcquisitionRequestDisplay
{
    public static string StatusLabel(string status) => status switch
    {
        "PendingPickup" => "Inbox",
        "AcceptedInPlugin" => "Working set",
        "Complete" => "Completed",
        _ => status,
    };

    public static IReadOnlyList<MarketAcquisitionBatchLineView> LinesFor(MarketAcquisitionRequestView request)
    {
        if (request.Lines.Count > 0)
            return request.Lines;

        return
        [
            new MarketAcquisitionBatchLineView
            {
                ItemId = request.ItemId,
                ItemName = request.ItemName,
                QuantityMode = request.QuantityMode,
                TargetQuantity = request.QuantityMode == "AllBelowThreshold" ? 0 : request.Quantity,
                MaxQuantity = request.QuantityMode == "AllBelowThreshold" ? request.Quantity : 0,
                HqPolicy = request.HqPolicy,
                MaxUnitPrice = request.MaxUnitPrice,
                GilCap = request.MaxTotalGil,
                Status = request.Status,
            },
        ];
    }

    public static string TitleFor(MarketAcquisitionRequestView request)
    {
        var lines = LinesFor(request);
        return lines.Count > 1 ? $"{lines.Count:N0} item batch" : ItemLabel(lines[0]);
    }

    public static string ItemLabel(MarketAcquisitionBatchLineView line) =>
        string.IsNullOrWhiteSpace(line.ItemName) ? $"Item {line.ItemId}" : line.ItemName;

    public static string QuantityDisplay(MarketAcquisitionBatchLineView line) =>
        line.QuantityMode == "AllBelowThreshold"
            ? line.MaxQuantity == 0 ? "All safe stock" : $"Max {line.MaxQuantity:N0}"
            : line.TargetQuantity.ToString("N0");

    public static string ModeDisplay(MarketAcquisitionBatchLineView line) =>
        line.QuantityMode == "AllBelowThreshold" ? "All below threshold" : line.QuantityMode;

    public static string MaxUnitDisplay(MarketAcquisitionBatchLineView line) =>
        line.MaxUnitPrice == 0 ? "-" : line.MaxUnitPrice.ToString("N0");

    public static string MaxUnitGilDisplay(MarketAcquisitionBatchLineView line) =>
        line.MaxUnitPrice == 0 ? "-" : $"{line.MaxUnitPrice:N0} gil";

    public static string GilCapDisplay(MarketAcquisitionBatchLineView line) =>
        line.GilCap == 0 ? "No cap" : line.GilCap.ToString("N0");

    public static bool IsEditable(MarketAcquisitionRequestView request)
    {
        if (request.Status is "Running" or "Complete" or "Failed" or "Rejected" or "Expired" or "Cancelled")
            return false;

        return string.IsNullOrWhiteSpace(request.LatestAttemptEventType) &&
            LinesFor(request).All(line => line.PurchasedQuantity == 0 && line.SpentGil == 0);
    }
}
