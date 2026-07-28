using System.Numerics;
using MarketMafioso.Windows;

namespace MarketMafioso.SpecTests.Windows;

internal static class WindowPlacementRecoveryTests
{
    public static void VerifyContract()
    {
        var recovered = WindowPlacementRecovery.TryRecoverTitleBar(
            new Vector2(700, -240),
            new Vector2(980, 930),
            Vector2.Zero,
            new Vector2(2560, 1440),
            24,
            out var position);

        Assert.True(recovered);
        Assert.Equal(new Vector2(700, 0), position);

        recovered = WindowPlacementRecovery.TryRecoverTitleBar(
            new Vector2(2400, 1500),
            new Vector2(980, 930),
            Vector2.Zero,
            new Vector2(2560, 1440),
            24,
            out position);

        Assert.True(recovered);
        Assert.Equal(new Vector2(1580, 1416), position);

        recovered = WindowPlacementRecovery.TryRecoverTitleBar(
            new Vector2(700, 30),
            new Vector2(980, 930),
            Vector2.Zero,
            new Vector2(2560, 1440),
            24,
            out position);

        Assert.False(recovered);
        Assert.Equal(new Vector2(700, 30), position);
    }
}
