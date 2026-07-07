using System;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class AcquisitionWorkbenchDraftMutation
{
    public static MarketAcquisitionQuickShopDraft ApplyMaxUnitPrice(
        MarketAcquisitionQuickShopDraft draft,
        int selectedLineIndex,
        uint maxUnitPrice)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (selectedLineIndex < 0 || selectedLineIndex >= draft.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        return ApplyPricing(
            draft,
            selectedLineIndex,
            maxUnitPrice,
            draft.Lines[selectedLineIndex].GilCap);
    }

    public static MarketAcquisitionQuickShopDraft ApplyPricing(
        MarketAcquisitionQuickShopDraft draft,
        int selectedLineIndex,
        uint maxUnitPrice,
        uint gilCap)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (selectedLineIndex < 0 || selectedLineIndex >= draft.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var lines = draft.Lines.ToList();
        lines[selectedLineIndex] = lines[selectedLineIndex] with
        {
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        };

        return draft.WithNextRevision() with { Lines = lines };
    }
}
