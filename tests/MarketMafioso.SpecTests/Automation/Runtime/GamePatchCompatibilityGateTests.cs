using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.SpecTests.Automation.Runtime;

public sealed class GamePatchCompatibilityGateTests
{
    [Fact]
    public void Evaluate_BlocksAChangedOrUnknownBuild()
    {
        var approved = GamePatchCompatibilityGate.Evaluate(
            "mmf.market-purchase-receive-packet",
            "2026.06.18.0000.0000",
            "2026.06.18.0000.0000");
        var changed = GamePatchCompatibilityGate.Evaluate(
            "mmf.market-purchase-receive-packet",
            "2026.06.18.0000.0000",
            "2026.07.28.0000.0000");
        var unknown = GamePatchCompatibilityGate.Evaluate(
            "mmf.market-purchase-receive-packet",
            "2026.06.18.0000.0000",
            "unknown");

        Assert.True(approved.IsApproved);
        Assert.False(changed.IsApproved);
        Assert.False(unknown.IsApproved);
        Assert.Equal(GamePatchCompatibility.FailureCode, "UnsupportedGameBuild");
    }
}
