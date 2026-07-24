using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Tests.MarketDiagnostics;

public sealed class RetainerSaleChatObserverTests
{
    [Theory]
    [InlineData("Rarefied Sykon Bavarois you put up for sale in the Limsa Lominsa markets has sold for 19,950 gil (after fees).", 19950)]
    [InlineData("Iron Ore you put up for sale in the Gridania markets have sold for 1,234,567 gil (after fees).", 1234567)]
    [InlineData("Un servant a vendu Minerai de fer pour 12 345 gil à Gridania.", 12345)]
    public void TryReadTotalGil_ParsesSupportedSaleMessages(string message, ulong expected)
    {
        Assert.True(RetainerSaleChatObserver.TryReadTotalGil(message, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryReadTotalGil_RejectsUnrelatedRetainerMessages()
    {
        Assert.False(RetainerSaleChatObserver.TryReadTotalGil(
            "Your retainer has completed a venture.",
            out _));
    }
}
