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

        var lines = draft.Lines.ToList();
        lines[selectedLineIndex] = lines[selectedLineIndex] with
        {
            MaxUnitPrice = maxUnitPrice,
        };

        return draft.WithNextRevision() with { Lines = lines };
    }
}
