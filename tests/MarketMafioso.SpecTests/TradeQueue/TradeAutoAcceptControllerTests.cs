using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeAutoAcceptControllerTests
{
    [Fact]
    public void ReceiverWaitsForPartnerThenReadiesAndConfirms()
    {
        var io = new FakeIo { IsTradeOpen = true, CanClickReady = true };
        var clock = new TestClock();
        var controller = Controller(io, clock);

        controller.Tick(enabled: true);
        clock.Advance(TradeQueueTimingOptions.DefaultActionDelayMilliseconds);
        controller.Tick(enabled: true);
        Assert.Equal(0, io.ReadyClicks);

        io.IsPartnerReadyForTrade = true;
        controller.Tick(enabled: true);
        clock.Advance(TradeQueueTimingOptions.DefaultActionDelayMilliseconds - 1);
        controller.Tick(enabled: true);
        Assert.Equal(0, io.ReadyClicks);

        clock.Advance(1);
        controller.Tick(enabled: true);
        Assert.Equal(1, io.ReadyClicks);

        io.CanClickReady = false;
        io.CanConfirmTrade = true;
        controller.Tick(enabled: true);
        clock.Advance(TradeQueueTimingOptions.DefaultActionDelayMilliseconds);
        controller.Tick(enabled: true);
        Assert.Equal(1, io.ConfirmClicks);
    }

    [Fact]
    public void ReceiverDoesNothingWhenDisabledOrTradeIsClosed()
    {
        var io = new FakeIo
        {
            IsPartnerReadyForTrade = true,
            CanClickReady = true,
            CanConfirmTrade = true,
        };
        var clock = new TestClock();
        var controller = Controller(io, clock);

        controller.Tick(enabled: false);
        clock.Advance(1_000);
        controller.Tick(enabled: false);
        Assert.Equal(0, io.ReadyClicks + io.ConfirmClicks);

        controller.Tick(enabled: true);
        clock.Advance(1_000);
        controller.Tick(enabled: true);
        Assert.Equal(0, io.ReadyClicks + io.ConfirmClicks);
    }

    [Fact]
    public void ConfirmationTakesPriorityOverReady()
    {
        var action = TradeAutoAcceptController.SelectAction(
            enabled: true,
            isTradeOpen: true,
            isPartnerReady: true,
            canReady: true,
            canConfirm: true);

        Assert.Equal(TradeAutoAcceptAction.Confirm, action);
    }

    private static TradeAutoAcceptController Controller(FakeIo io, TestClock clock) =>
        new(
            io,
            new TradeQueueTimingOptions(),
            TestPluginLog.Create(),
            clock.Read);

    private sealed class FakeIo : ITradeAutoAcceptIo
    {
        public bool IsTradeOpen { get; set; }
        public bool IsPartnerReadyForTrade { get; set; }
        public bool CanClickReady { get; set; }
        public bool CanConfirmTrade { get; set; }
        public int ReadyClicks { get; private set; }
        public int ConfirmClicks { get; private set; }

        public bool TryClickReady(out string error)
        {
            error = string.Empty;
            ReadyClicks++;
            return true;
        }

        public bool TryConfirmTrade(out string error)
        {
            error = string.Empty;
            ConfirmClicks++;
            return true;
        }
    }

    private sealed class TestClock
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public DateTimeOffset Read() => now;
        public void Advance(int milliseconds) => now = now.AddMilliseconds(milliseconds);
    }

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(bool) ? false : null;
    }
}
