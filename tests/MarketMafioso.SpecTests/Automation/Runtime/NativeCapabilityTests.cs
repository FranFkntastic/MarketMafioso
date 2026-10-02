using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.SpecTests.Automation.Runtime;

public sealed class NativeCapabilityTests
{
    [Theory]
    [InlineData("2026.09.01.0000.0000", 0x1000)]
    [InlineData("2026.09.15.0000.0000", 0x2000)]
    [InlineData("unknown", 0x3000)]
    public void EquivalentResolvedCapabilitiesSurviveBuildAndAddressChanges(string diagnosticBuild, int relocatedAddress)
    {
        // The build label is diagnostic metadata. Only the actual capability
        // evidence enters readiness; there is no approved-version input.
        Assert.NotEmpty(diagnosticBuild);
        Assert.Equal((nint)relocatedAddress,
            NativeCapability.ResolveUnique([(nint)relocatedAddress], "Trade item offering"));
        NativeCapability.RequireTradeContextLayout(0x5A0, 0x28, 0x578);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrAmbiguousSignatureDoesNotProduceACallableAddress(int count)
    {
        var matches = Enumerable.Range(1, count).Select(index => (nint)(index * 0x1000)).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => NativeCapability.ResolveUnique(matches, "Trade item offering"));
        Assert.Contains("exactly one", error.Message);
    }

    [Fact]
    public void NullAddressAndMissingDependencyRemainUnavailable()
    {
        Assert.Throws<InvalidOperationException>(() => NativeCapability.ResolveUnique([(nint)0], "Trade"));
        var error = Assert.Throws<InvalidOperationException>(() => NativeCapability.RequireAddress(0, "Purchase evidence hook"));
        Assert.Contains("Purchase evidence hook", error.Message);
        NativeCapability.RequireAddress(0x1000, "Independent trade capability");
    }

    [Theory]
    [InlineData(0x5A0, -1, 0x578)]
    [InlineData(0x5A0, 0x28, 4)]
    [InlineData(0x570, 0x28, 0x578)]
    public void IncompatibleTradeLayoutFailsBeforeNativeCall(int agentSize, int contextOffset, int contextSize)
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeCapability.RequireTradeContextLayout(agentSize, contextOffset, contextSize));
        Assert.Contains("layout is unsupported", error.Message);
    }

    [Fact]
    public void CurrentSdkExposesTheTradeContextWithoutAHardcodedAgentOffset()
    {
        NativeCapability.RequireTradeContextLayout(Marshal.SizeOf<AgentTrade>(),
            checked((int)Marshal.OffsetOf<AgentTrade>(nameof(AgentTrade.InventoryContextEvent))),
            Marshal.SizeOf<AgentInventoryContext.InventoryContextEvent>());
    }
}
