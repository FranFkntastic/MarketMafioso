using System.Reflection;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
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
        QualityLoweringStartupFailureReleasesAutoConfirm();
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
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
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
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
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
        clock.Advance(TimeSpan.FromMilliseconds(300));
        runner.Tick();
        runner.Tick();
        runner.Tick();
        io.IsTradeOpenValue = false;
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.VerifyingInventory, runner.Snapshot.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.Equal(2, Assert.Single(queue).Quantity);
        Assert.True(runner.CanResume);
    }

    private static void StopReleasesAutoConfirmAndPreservesQueue()
    {
        var queue = Queue(2);
        var stopRequests = new HashSet<string>();
        using var coordinator = Coordinator(stopRequests);
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            new FakeIo(Inventory(2)),
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create());

        runner.Start();
        runner.Stop();

        Assert.Equal(TradeQueueExecutionState.Stopped, runner.Snapshot.State);
        Assert.Equal(2, Assert.Single(queue).Quantity);
        Assert.DoesNotContain("MarketMafioso", stopRequests);
    }

    private static void QualityLoweringStartupFailureReleasesAutoConfirm()
    {
        var queue = Queue(2);
        var stopRequests = new HashSet<string>();
        using var coordinator = Coordinator(stopRequests);
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            new FakeIo(Inventory(2)),
            new FakeQualityLowering(failOnBegin: true),
            coordinator,
            TestPluginLog.Create());

        var result = runner.Start();

        Assert.False(result.Success);
        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
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
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
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

    [Fact]
    public void Runner_HonorsConfiguredTradeCommandRetry()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        var timing = new TradeQueueTimingOptions
        {
            TradeRetryMilliseconds = 1_500,
        };
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            timing,
            () => { },
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        runner.Tick();
        runner.Tick();
        Assert.Equal(1, io.OpenTradeAttempts);

        clock.Advance(TimeSpan.FromMilliseconds(1_499));
        runner.Tick();
        Assert.Equal(1, io.OpenTradeAttempts);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        runner.Tick();
        Assert.Equal(2, io.OpenTradeAttempts);
    }

    [Fact]
    public void Runner_HonorsConfiguredActionDelayAfterTradeOpens()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        var timing = new TradeQueueTimingOptions
        {
            ActionDelayMilliseconds = 400,
        };
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            timing,
            () => { },
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        runner.Tick();
        runner.Tick();
        io.IsTradeOpenValue = true;
        runner.Tick();

        clock.Advance(TimeSpan.FromMilliseconds(399));
        runner.Tick();
        Assert.Equal(0, io.OfferItemAttempts);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        runner.Tick();
        Assert.Equal(1, io.OfferItemAttempts);
    }

    [Fact]
    public void Runner_CheckpointsAndImmediatelyContinuesThenResumesOnlyVerifiedRemainder()
    {
        var queue = Enumerable.Range(0, 6)
            .Select(index => new TradeQueueItem
            {
                ItemId = (uint)(100 + index),
                ItemName = $"Item {index + 1}",
                Quantity = 1,
            })
            .ToList();
        var io = new FakeIo(
            queue.Select((item, index) =>
                    new TradeQueueInventoryStack(0, index, item.ItemId, item.ItemName, false, 1))
                .ToArray());
        var clock = new TestClock();
        var saves = 0;
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => saves++,
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        var runId = runner.Snapshot.RunId;
        Assert.False(string.IsNullOrWhiteSpace(runId));
        runner.Tick();
        runner.Tick();
        Assert.Equal(1, io.OpenTradeAttempts);
        AdvanceOpenTradeToVerification(runner, io, clock);

        io.IsTradeOpenValue = false;
        io.Inventory = io.Inventory.Where(stack => stack.ItemId == 105).ToArray();
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.OpeningTrade, runner.Snapshot.State);
        Assert.Equal(2, io.OpenTradeAttempts);
        Assert.Equal(1, runner.Snapshot.CompletedBatchCount);
        Assert.Equal(5, runner.Snapshot.CompletedUnitCount);
        Assert.Equal(6, runner.Snapshot.InitialUnitCount);
        Assert.Equal(1, Assert.Single(queue).Quantity);
        Assert.Equal(1, saves);

        AdvanceOpenTradeToVerification(runner, io, clock);
        io.IsTradeOpenValue = false;
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.VerifyingInventory, runner.Snapshot.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.True(runner.CanResume);
        Assert.Equal(1, runner.Snapshot.CompletedBatchCount);
        Assert.Equal(5, runner.Snapshot.CompletedUnitCount);
        Assert.Equal(1, Assert.Single(queue).Quantity);

        var resume = runner.Start();
        Assert.True(resume.Success);
        Assert.Contains("Resumed", resume.Message);
        Assert.Equal(TradeQueueExecutionState.NormalizingQuality, runner.Snapshot.State);
        Assert.Equal(2, runner.Snapshot.BatchNumber);
        Assert.Equal(1, runner.Snapshot.CompletedBatchCount);
        Assert.Equal(6, runner.Snapshot.InitialUnitCount);
        Assert.Equal(runId, runner.Snapshot.RunId);
    }

    [Fact]
    public void Runner_StartsForAnExactBridgeResolvedPartnerWithoutAmbientTargetState()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create());

        Assert.True(io.TryGetPartner("Recipient", "Siren", out var exactPartner));
        var result = runner.Start(exactPartner);

        Assert.True(result.Success);
        Assert.Equal("Recipient", runner.Snapshot.PartnerName);
        Assert.False(string.IsNullOrWhiteSpace(runner.Snapshot.RunId));
    }

    [Fact]
    public void Runner_AcceptsInventoryEvidenceThatSettlesAfterTradeClosure()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        runner.Tick();
        runner.Tick();
        AdvanceOpenTradeToVerification(runner, io, clock);

        io.IsTradeOpenValue = false;
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.VerifyingInventory, runner.Snapshot.State);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        io.Inventory = [];
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Completed, runner.Snapshot.State);
        Assert.Empty(queue);
    }

    [Fact]
    public void Runner_TreatsAnEditedCheckpointAsANewRun()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        runner.Stop();
        Assert.True(runner.CanResume);
        Assert.True(runner.HasResumeCheckpoint);

        io.HasSelectedPartner = false;
        Assert.False(runner.CanResume);
        Assert.True(runner.HasResumeCheckpoint);
        io.HasSelectedPartner = true;

        queue[0].Quantity = 1;
        io.Inventory = Inventory(1);
        Assert.False(runner.CanResume);
        Assert.False(runner.HasResumeCheckpoint);

        var restart = runner.Start();
        Assert.True(restart.Success);
        Assert.Contains("Started", restart.Message);
        Assert.Equal(1, runner.Snapshot.InitialUnitCount);
        Assert.Equal(0, runner.Snapshot.CompletedUnitCount);
        Assert.Equal(0, runner.Snapshot.CompletedBatchCount);
        Assert.Equal(1, runner.Snapshot.BatchNumber);
    }

    [Fact]
    public void Runner_DoesNotImposeAQueueWideTimeoutOnBoundedQualityAutomation()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            queue,
            new TradeQueueTimingOptions(),
            () => { },
            io,
            new FakeQualityLowering(activeAdvances: 2),
            coordinator,
            TestPluginLog.Create(),
            clock.Read);

        Assert.True(runner.Start().Success);
        clock.Advance(TimeSpan.FromMinutes(3));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.NormalizingQuality, runner.Snapshot.State);

        clock.Advance(TimeSpan.FromMinutes(3));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.NormalizingQuality, runner.Snapshot.State);

        clock.Advance(TimeSpan.FromMinutes(3));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.OpeningTrade, runner.Snapshot.State);
    }

    private static void AdvanceOpenTradeToVerification(
        TradeQueueRunner runner,
        FakeIo io,
        TestClock clock)
    {
        io.IsTradeOpenValue = true;
        for (var index = 0;
             index < 30 && runner.Snapshot.State != TradeQueueExecutionState.VerifyingInventory;
             index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            runner.Tick();
        }

        Assert.Equal(TradeQueueExecutionState.VerifyingInventory, runner.Snapshot.State);
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
        private bool isTradeOpenValue;
        public bool IsTradeOpenValue
        {
            get => isTradeOpenValue;
            set
            {
                if (isTradeOpenValue && !value)
                {
                    OfferedSlotCount = 0;
                    IsNumericInputOpen = false;
                }

                isTradeOpenValue = value;
            }
        }
        public bool IsTradeOpen => IsTradeOpenValue;
        public bool IsNumericInputOpen { get; private set; }
        public int OfferedSlotCount { get; private set; }
        public int SubmittedGil { get; private set; }
        public int OpenTradeAttempts { get; private set; }
        public int OfferItemAttempts { get; private set; }
        public bool HasSelectedPartner { get; set; } = true;
        private bool gilInputRequested;

        public IReadOnlyList<TradeQueueInventoryStack> ScanTradeableInventory() => Inventory;

        public IReadOnlyList<TradeQueuePartner> GetAvailablePartners() =>
            [new(1, "Recipient", 2, "Siren")];

        public bool TryGetSelectedPartner(out TradeQueuePartner partner)
        {
            partner = new(1, "Recipient", 2, "Siren");
            return HasSelectedPartner;
        }

        public bool TryGetPartner(string name, string homeWorld, out TradeQueuePartner partner)
        {
            if (name == "Recipient" && homeWorld == "Siren")
            {
                partner = new(1, name, 2, homeWorld);
                return true;
            }

            partner = new(0, string.Empty, 0);
            return false;
        }

        public bool PartnerIsAvailable(TradeQueuePartner partner) => true;
        public bool CanClickReady => IsTradeOpen;
        public bool CanConfirmTrade => IsTradeOpen;

        public bool TryOpenTrade(TradeQueuePartner partner)
        {
            OpenTradeAttempts++;
            return true;
        }

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
            OfferItemAttempts++;
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

    private sealed class FakeQualityLowering(
        bool failOnBegin = false,
        int activeAdvances = 0) : IItemQualityLoweringAutomation
    {
        private int remainingActiveAdvances = activeAdvances;

        public ItemQualityLoweringAutomationSnapshot Snapshot { get; private set; } =
            new(ItemQualityLoweringAutomationState.Idle, "Idle.", null, 0, false);

        public ItemQualityLoweringAutomationSnapshot Begin(
            IReadOnlyList<ItemQualityLoweringRequirement> requested)
        {
            if (failOnBegin)
            {
                Snapshot = new(
                    ItemQualityLoweringAutomationState.Failed,
                    "Quality lowering could not start.",
                    null,
                    0,
                    false);
                return Snapshot;
            }

            Snapshot = new(
                ItemQualityLoweringAutomationState.Preparing,
                "Checking quality.",
                null,
                0,
                true);
            return Snapshot;
        }

        public ItemQualityLoweringAutomationSnapshot Advance(Func<bool> mutationStillAuthorized)
        {
            if (!mutationStillAuthorized())
            {
                Snapshot = new(
                    ItemQualityLoweringAutomationState.Failed,
                    "Authorization lost.",
                    null,
                    0,
                    false);
                return Snapshot;
            }

            if (remainingActiveAdvances-- > 0)
            {
                Snapshot = new(
                    ItemQualityLoweringAutomationState.Preparing,
                    "Quality normalization is making progress.",
                    null,
                    remainingActiveAdvances,
                    true);
                return Snapshot;
            }

            Snapshot = new(
                ItemQualityLoweringAutomationState.Completed,
                "Quality ready.",
                null,
                0,
                false);
            return Snapshot;
        }

        public void Stop(string message = "Quality lowering stopped.")
        {
            Snapshot = new(ItemQualityLoweringAutomationState.Stopped, message, null, 0, false);
        }
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
