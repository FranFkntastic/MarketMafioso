using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeQueueRunnerTests
{
    [Fact]
    public void Runner_RequiresExactCompletionEvidenceAndRestoresAutomationOwnership()
    {
        RemovesBatchOnlyAfterExactInventoryDelta();
        CanceledTradeLeavesUnverifiedQuantityQueued();
        StopReleasesAutoConfirmAndPreservesQueue();
        GilUsesCurrencyInputAndExactBalanceEvidence();
    }

    private static void RemovesBatchOnlyAfterExactInventoryDelta()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        var stopRequests = new HashSet<string>();
        using var coordinator = Coordinator(stopRequests);
        using var runner = new TradeQueueRunner(
            queue,
            () => { },
            io,
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        Assert.Contains("MarketMafioso", stopRequests);
        runner.Tick();
        io.IsTradeOpenValue = true;
        clock.Advance(TimeSpan.FromSeconds(4));
        runner.Tick();
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        runner.Tick();
        io.IsTradeOpenValue = false;
        io.Inventory = [];
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Completed, runner.Snapshot.State);
        Assert.Empty(queue);
        Assert.DoesNotContain("MarketMafioso", stopRequests);
    }

    private static void CanceledTradeLeavesUnverifiedQuantityQueued()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            () => { },
            io,
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        runner.Start();
        runner.Tick();
        io.IsTradeOpenValue = true;
        clock.Advance(TimeSpan.FromSeconds(4));
        runner.Tick();
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        runner.Tick();
        io.IsTradeOpenValue = false;
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.Equal(2, Assert.Single(queue).Quantity);
    }

    private static void StopReleasesAutoConfirmAndPreservesQueue()
    {
        var queue = Queue(2);
        var stopRequests = new HashSet<string>();
        using var coordinator = Coordinator(stopRequests);
        using var runner = new TradeQueueRunner(
            queue,
            () => { },
            new FakeIo(Inventory(2)),
            coordinator,
            TestPluginLog.Create());

        runner.Start();
        runner.Stop();

        Assert.Equal(TradeQueueExecutionState.Stopped, runner.Snapshot.State);
        Assert.Equal(2, Assert.Single(queue).Quantity);
        Assert.DoesNotContain("MarketMafioso", stopRequests);
    }

    private static void GilUsesCurrencyInputAndExactBalanceEvidence()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = TradeQueuePlanner.GilItemId, ItemName = "Gil", Quantity = 600_000 },
        };
        var io = new FakeIo(
        [
            new(uint.MaxValue, -1, TradeQueuePlanner.GilItemId, "Gil", false, 3_757_109),
        ]);
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            () => { },
            io,
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        runner.Tick();
        io.IsTradeOpenValue = true;
        clock.Advance(TimeSpan.FromSeconds(4));
        runner.Tick();
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        runner.Tick();
        io.IsTradeOpenValue = false;
        io.Inventory =
        [
            new(uint.MaxValue, -1, TradeQueuePlanner.GilItemId, "Gil", false, 3_157_109),
        ];
        runner.Tick();

        Assert.Equal(600_000, io.SubmittedGil);
        Assert.Equal(TradeQueueExecutionState.Completed, runner.Snapshot.State);
        Assert.Empty(queue);
    }

    private static List<TradeQueueItem> Queue(int quantity) =>
    [
        new() { ItemId = 100, ItemName = "Cobalt Ingot", Quantity = quantity },
    ];

    private static IReadOnlyList<TradeQueueInventoryStack> Inventory(int quantity) =>
    [
        new(0, 0, 100, "Cobalt Ingot", false, quantity),
    ];

    private static ExternalAutomationCoordinator Coordinator(HashSet<string> stopRequests) =>
        new(new FakePluginDataStore(stopRequests), TestPluginLog.Create());

    private sealed class FakeIo(IReadOnlyList<TradeQueueInventoryStack> inventory) : ITradeQueueIo
    {
        public IReadOnlyList<TradeQueueInventoryStack> Inventory { get; set; } = inventory;
        public bool IsTradeOpenValue { get; set; }
        public bool IsTradeOpen => IsTradeOpenValue;
        public bool IsNumericInputOpen { get; private set; }
        public int OfferedSlotCount { get; private set; }
        public int SubmittedGil { get; private set; }
        private bool gilInputRequested;

        public IReadOnlyList<TradeQueueInventoryStack> ScanTradeableInventory() => Inventory;

        public bool TryGetFocusPartner(out TradeQueuePartner partner)
        {
            partner = new(1, "Recipient", 2);
            return true;
        }

        public bool FocusPartnerMatches(TradeQueuePartner partner) => true;

        public bool TryOpenTrade(TradeQueuePartner partner) => true;

        public bool TryOpenGilInput(out string error)
        {
            error = string.Empty;
            gilInputRequested = true;
            IsNumericInputOpen = true;
            return true;
        }

        public bool TryOfferItem(TradeQueueBatchLine line, out string error)
        {
            error = string.Empty;
            if (line.SourceStackQuantity > 1)
                IsNumericInputOpen = true;
            else
                OfferedSlotCount++;
            return true;
        }

        public bool TrySubmitQuantity(int quantity, out string error)
        {
            error = string.Empty;
            IsNumericInputOpen = false;
            if (gilInputRequested)
            {
                SubmittedGil = quantity;
                gilInputRequested = false;
            }
            else
            {
                OfferedSlotCount++;
            }
            return true;
        }

        public bool TryClickReady(out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryConfirmTrade(out string error)
        {
            error = string.Empty;
            return true;
        }
    }

    private sealed class TestClock
    {
        private DateTimeOffset now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Read() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class FakePluginDataStore(HashSet<string> stopRequests) : IPluginDataStore
    {
        public bool TryGetData<T>(string key, out T? data)
            where T : class
        {
            if (key == "YesAlready.StopRequests")
            {
                data = (T)(object)stopRequests;
                return true;
            }

            data = null;
            return false;
        }
    }

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
