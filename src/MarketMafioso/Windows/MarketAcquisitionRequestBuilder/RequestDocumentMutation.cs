using System;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestDocumentMutation
{
    public static MarketAcquisitionRequestDocument ApplyMaxUnitPrice(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        uint maxUnitPrice)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        return ApplyPricing(
            document,
            selectedLineIndex,
            maxUnitPrice,
            document.Lines[selectedLineIndex].GilCap);
    }

    public static MarketAcquisitionRequestDocument ApplyPricing(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        uint maxUnitPrice,
        uint gilCap)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var line = document.Lines[selectedLineIndex];
        return ApplyLineEdit(
            document,
            selectedLineIndex,
            line.QuantityMode,
            line.TargetQuantity,
            line.MaxQuantity,
            line.HqPolicy,
            maxUnitPrice,
            gilCap);
    }

    public static MarketAcquisitionRequestDocument ApplyLineEdit(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        string quantityMode,
        uint targetQuantity,
        uint maxQuantity,
        string hqPolicy,
        uint maxUnitPrice,
        uint gilCap)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var lines = document.Lines.ToList();
        lines[selectedLineIndex] = lines[selectedLineIndex] with
        {
            QuantityMode = string.IsNullOrWhiteSpace(quantityMode) ? "AllBelowThreshold" : quantityMode,
            TargetQuantity = targetQuantity,
            MaxQuantity = maxQuantity,
            HqPolicy = string.IsNullOrWhiteSpace(hqPolicy) ? "Either" : hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        };

        return MarkEdited(document) with { Lines = lines };
    }

    private static MarketAcquisitionRequestDocument MarkEdited(MarketAcquisitionRequestDocument document) =>
        document.WithNextRevision(string.IsNullOrWhiteSpace(document.RemoteRequestId)
            ? "NewDraft"
            : "LocalEdits");
}
