using System;
using System.Collections.Generic;

namespace MarketMafioso.TradeQueue;

internal readonly record struct TradeButtonCandidate(nint Address, string Label, bool Visible, bool Enabled);

internal static class TradeButtonIdentity
{
    internal static nint FindUnique(IReadOnlyList<TradeButtonCandidate> buttons, string expectedLabel)
    {
        if (string.IsNullOrWhiteSpace(expectedLabel))
            return 0;

        TradeButtonCandidate? match = null;
        foreach (var button in buttons)
        {
            if (button.Address == 0 || !button.Visible ||
                !string.Equals(button.Label, expectedLabel, StringComparison.Ordinal))
                continue;
            if (match is not null && match.Value.Address != button.Address)
                return 0;
            match = button;
        }
        return match is { Enabled: true } found ? found.Address : 0;
    }
}
