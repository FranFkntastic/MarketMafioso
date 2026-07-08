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

        var lines = document.Lines.ToList();
        lines[selectedLineIndex] = lines[selectedLineIndex] with
        {
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        };

        return document.WithNextRevision("LocalEdits") with { Lines = lines };
    }
}
