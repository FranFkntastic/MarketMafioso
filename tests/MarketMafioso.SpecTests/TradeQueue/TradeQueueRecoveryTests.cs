using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed partial class TradeQueueRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOfferDoesNotBlockOwnedCancellation(bool resumed)
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2)) { IsTradeOpenValue = true, OfferUnavailable = true };
        var recovery = new TradeQueueRecoveryState { CancellationPending = resumed, PartnerEntityId = 1 };
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(),
            policy: new() { NormalizeHighQualityItems = false }, recovery: recovery);
        if (resumed)
            runner.Tick();
        else
        {
            Assert.True(runner.Start().Success);
            runner.Stop();
        }
        Assert.Equal(1, io.CancelTradeAttempts);
        Assert.False(io.IsTradeOpen);
        Assert.False(runner.IsActive);
        Assert.False(recovery.CancellationPending);
        Assert.Equal(2, Assert.Single(queue).Quantity);
        Assert.Equal(0, io.ConfirmAttempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99u)]
    public void InvalidOfferDoesNotAuthorizeCancellingAnUnknownOrDifferentRecipient(uint? recipient)
    {
        var io = new FakeIo(Inventory(2))
        {
            IsTradeOpenValue = true, OfferUnavailable = true, ObservedPartnerEntityId = recipient,
        };
        var recovery = new TradeQueueRecoveryState { CancellationPending = true, PartnerEntityId = 1 };
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), recovery: recovery);
        runner.Tick();
        Assert.Equal(0, io.CancelTradeAttempts);
        Assert.True(io.IsTradeOpen);
        Assert.Equal(recipient == null, recovery.CancellationPending);
    }

    [Fact]
    public void StopSupersedesRebuildWhileCheckpointClearingFails()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(4)) { IsTradeOpenValue = true };
        var clock = new TestClock();
        var recovery = new TradeQueueRecoveryState();
        var suppression = new HashSet<string>();
        var failClear = true;
        using var coordinator = Coordinator(suppression);
        using var runner = new TradeQueueRunner(queue, new(), () =>
        {
            if (failClear && !recovery.CancellationPending)
                throw new IOException("Checkpoint clearing temporarily unavailable.");
        }, io, new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false }, recovery);
        Assert.True(runner.Start().Success);
        io.SetManualOffer(new TradeOfferedItem(100, false, 1, 0, 0));
        runner.Tick();
        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.ClosingTrade, runner.Snapshot.State);
        Assert.False(io.IsTradeOpen);
        Assert.True(recovery.CancellationPending);

        runner.Stop("User stopped the pending rebuild.");
        Assert.True(runner.IsActive);
        Assert.Contains("MarketMafioso", suppression);
        failClear = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Stopped, runner.Snapshot.State);
        Assert.Contains("User stopped", runner.Snapshot.Message);
        Assert.False(runner.IsActive);
        Assert.False(recovery.CancellationPending);
        Assert.DoesNotContain("MarketMafioso", suppression);
        Assert.Equal(0, io.OpenTradeAttempts);
        Assert.Equal(0, io.OfferItemAttempts);
        Assert.Equal(0, io.ConfirmAttempts);
        Assert.Equal(2, Assert.Single(queue).Quantity);
        runner.Tick();
        Assert.Equal(0, io.OpenTradeAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationStorageFailureRetainsOwnershipAndRetries(bool failBeforeCancel)
    {
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        var recovery = new TradeQueueRecoveryState();
        var suppression = new HashSet<string>();
        var failStorage = false;
        using var coordinator = Coordinator(suppression);
        using var runner = new TradeQueueRunner(Queue(2), new(), () =>
        {
            if (failStorage && recovery.CancellationPending == failBeforeCancel)
                throw new IOException("Storage temporarily unavailable.");
        }, io, new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read, recovery: recovery);
        runner.Start();
        io.IsTradeOpenValue = true;
        failStorage = true;

        runner.Stop();
        Assert.True(runner.IsActive);
        Assert.True(recovery.CancellationPending);
        Assert.Contains("MarketMafioso", suppression);
        Assert.Equal(failBeforeCancel ? 0 : 1, io.CancelTradeAttempts);
        Assert.Equal(TradeQueueExecutionState.ClosingTrade, runner.Snapshot.State);

        failStorage = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        runner.Tick();
        Assert.False(runner.IsActive);
        Assert.False(recovery.CancellationPending);
        Assert.Equal(1, io.CancelTradeAttempts);
        Assert.DoesNotContain("MarketMafioso", suppression);
    }

    [Fact]
    public void ReloadDoesNotCancelADifferentRecipientsTrade()
    {
        var io = new FakeIo(Inventory(2)) { IsTradeOpenValue = true };
        var recovery = new TradeQueueRecoveryState { CancellationPending = true, PartnerEntityId = 99 };
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), recovery: recovery);
        runner.Tick();
        Assert.Equal(0, io.CancelTradeAttempts);
        Assert.True(io.IsTradeOpen);
        Assert.False(runner.IsActive);
        Assert.False(recovery.CancellationPending);
    }

    [Fact]
    public void PendingCancellationSurvivesReloadWithoutReenablingAutoConfirmation()
    {
        var io = new FakeIo(Inventory(2)) { IsTradeOpenValue = true, CancelEnabled = false };
        var queue = Queue(2);
        var clock = new TestClock();
        var recovery = new TradeQueueRecoveryState();
        var suppression = new HashSet<string>();
        var coordinator = Coordinator(suppression);
        var runner = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read, recovery: recovery);
        runner.Start();
        runner.Dispose();
        coordinator.Dispose();
        Assert.True(recovery.CancellationPending);
        Assert.Contains("MarketMafioso", suppression);

        recovery = Newtonsoft.Json.JsonConvert.DeserializeObject<TradeQueueRecoveryState>(
            Newtonsoft.Json.JsonConvert.SerializeObject(recovery))!;

        using var nextCoordinator = Coordinator(suppression);
        using var restored = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), nextCoordinator, TestPluginLog.Create(), clock.Read, recovery: recovery);
        Assert.True(restored.IsActive);
        Assert.Equal(TradeQueueExecutionState.ClosingTrade, restored.Snapshot.State);
        io.CancelEnabled = true;
        restored.Tick();
        Assert.False(recovery.CancellationPending);
        Assert.False(restored.IsActive);
        Assert.DoesNotContain("MarketMafioso", suppression);
        Assert.Equal(2, Assert.Single(queue).Quantity);
    }

    [Fact]
    public void FreshStackPlanRetainsIntentAndRebasesCompletionEvidence()
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(4));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        Assert.True(runner.Start().Success);
        // The original location and full-stack size both changed before dispatch.
        io.Inventory = [new(1, 9, 100, "Cobalt Ingot", false, 7)];
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(1u, Assert.Single(io.ObserveOffer()!.Items).SourceContainerId);
        Assert.Equal(9, Assert.Single(io.ObserveOffer()!.Items).SourceSlotIndex);
        Assert.Equal(2, io.LastSubmittedQuantity);
        io.Inventory = [new(1, 9, 100, "Cobalt Ingot", false, 5)];
        io.IsTradeOpenValue = false;
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.Completed, runner.Snapshot.State);
        Assert.Equal(2, runner.Snapshot.CompletedUnitCount);
        Assert.Empty(queue);
    }

    [Fact]
    public void MatchingManualOfferDoesNotGetSubmittedAgain()
    {
        var io = new FakeIo(Inventory(4)) { IsTradeOpenValue = true };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        io.SetManualOffer(new TradeOfferedItem(100, false, 2, 0, 0));
        runner.Tick();
        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.WaitingForPartner, runner.Snapshot.State);
        Assert.Equal(0, io.OfferItemAttempts);
    }

    [Fact]
    public void ManualRemovalBeforeLockRebuildsTheMissingOffer()
    {
        var io = new FakeIo(Inventory(4)) { IsTradeOpenValue = true, ReadyEnabled = false };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        io.SetManualOffer(new TradeOfferedItem(100, false, 2, 0, 0));
        runner.Tick();
        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.WaitingForPartner, runner.Snapshot.State);
        io.SetManualOffer();
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.OfferingItems, runner.Snapshot.State);
        io.ReadyEnabled = true;
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(1, io.OfferItemAttempts);
        Assert.Equal(2, Assert.Single(io.ObserveOffer()!.Items).Quantity);
    }

    [Fact]
    public void ReducedDialogLimitReplansInsteadOfClampingTheQueuedQuantity()
    {
        var io = new FakeIo(Inventory(2)) { IsTradeOpenValue = true, QuantityMaximum = 1 };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        for (var index = 0; index < 3; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            runner.Tick();
        }
        Assert.Equal(1, io.OfferItemAttempts);
        Assert.Equal(0, io.QuantitySubmissions);
        io.Inventory = [new(0, 0, 100, "Cobalt Ingot", false, 1), new(0, 1, 100, "Cobalt Ingot", false, 1)];
        clock.Advance(TimeSpan.FromSeconds(31));
        runner.Tick();
        Assert.Equal(1, io.CancelTradeAttempts);
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(2, io.ObserveOffer()!.Items.Sum(item => item.Quantity));
        Assert.Equal(2, io.ObserveOffer()!.Items.Count);
    }

    [Fact]
    public void LostInFlightOfferReopensBeforeResubmitting()
    {
        var io = new FakeIo(Inventory(2)) { IsTradeOpenValue = true };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        for (var index = 0; index < 5 && io.QuantitySubmissions == 0; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            runner.Tick();
        }
        Assert.Equal(1, io.QuantitySubmissions);
        io.SetManualOffer(); // Removed before the pending operation observes its acknowledgement.
        clock.Advance(TimeSpan.FromSeconds(31));
        runner.Tick();
        Assert.Equal(1, io.CancelTradeAttempts);
        Assert.False(io.IsTradeOpen);
        Assert.Equal(1, io.OfferItemAttempts);
        Assert.Equal(0, io.ConfirmAttempts);
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(2, io.OfferItemAttempts);
        Assert.Equal(2, Assert.Single(io.ObserveOffer()!.Items).Quantity);
    }

    [Fact]
    public void SplitSourceRebuildsIntoLegalFiveSlotBatches()
    {
        var queue = Queue(6);
        var io = new FakeIo(Inventory(6)) { IsTradeOpenValue = true };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        io.Inventory = Enumerable.Range(0, 6)
            .Select(slot => new TradeQueueInventoryStack(0, slot, 100, "Cobalt Ingot", false, 1)).ToArray();
        for (var index = 0; index < 20 && io.CancelTradeAttempts == 0; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            runner.Tick();
        }
        Assert.Equal(1, io.CancelTradeAttempts);
        Assert.Equal(0, io.ConfirmAttempts);
        Assert.Equal(6, Assert.Single(queue).Quantity);
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(5, io.ObserveOffer()!.Items.Count);
        Assert.All(io.ObserveOffer()!.Items, item => Assert.Equal(1, item.Quantity));
        io.Inventory = [new(0, 5, 100, "Cobalt Ingot", false, 1)];
        io.IsTradeOpenValue = false;
        runner.Tick();
        Assert.Equal(1, Assert.Single(queue).Quantity);
        Assert.Equal(5, runner.Snapshot.CompletedUnitCount);
    }

    [Fact]
    public void PartialManualOfferReopensWhenItOccupiesTheOnlySource()
    {
        var io = new FakeIo(Inventory(4)) { IsTradeOpenValue = true };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read,
            new() { NormalizeHighQualityItems = false });
        runner.Start();
        io.SetManualOffer(new TradeOfferedItem(100, false, 1, 0, 0));
        runner.Tick();
        clock.Advance(TimeSpan.FromSeconds(1));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.OpeningTrade, runner.Snapshot.State);
        Assert.Equal(1, io.CancelTradeAttempts);
        Assert.Equal(0, io.ConfirmAttempts);
        AdvanceOpenTradeToVerification(runner, io, clock);
        Assert.Equal(2, io.LastSubmittedQuantity);
    }

    [Fact]
    public void RemainingPlanExcludesAlreadyOfferedSourcesAndCountsManualProgress()
    {
        var inventory = new TradeQueueInventoryStack[]
        {
            new(0, 0, 100, "Cobalt Ingot", false, 10),
            new(1, 3, 100, "Cobalt Ingot", false, 8),
        };
        var batch = TradeQueuePlanner.BuildNextBatch(Queue(5), inventory);
        var offer = new TradeOfferObservation(1, 0, false, [new(100, false, 2, 0, 0)]);
        var line = TradeQueueOfferProgress.NextLine(batch, offer, inventory);
        Assert.NotNull(line);
        Assert.Equal(1u, line.ContainerId);
        Assert.Equal(3, line.SlotIndex);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(8, line.SourceStackQuantity);
        Assert.Null(offer.Mismatch(new(1, "Recipient", 2), batch, allowPartial: true));
        Assert.NotNull((offer with { Items = [new(100, false, 6)] }).Mismatch(new(1, "Recipient", 2), batch, allowPartial: true));
        Assert.Null(TradeQueueOfferProgress.NextLine(batch, offer with { Items = [new(100, false, 2)] }, inventory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopRetainsOwnershipUntilClosureIsObserved(bool cancelEnabled)
    {
        var queue = Queue(2);
        var io = new FakeIo(Inventory(2)) { CancelEnabled = cancelEnabled, CancelClosesTrade = false };
        var clock = new TestClock();
        var suppression = new HashSet<string>();
        using var coordinator = Coordinator(suppression);
        using var runner = new TradeQueueRunner(queue, new(), () => { }, io,
            new FakeQualityLowering(), coordinator, TestPluginLog.Create(), clock.Read);
        Assert.True(runner.Start().Success);
        io.IsTradeOpenValue = true;

        runner.Stop();

        Assert.Equal(TradeQueueExecutionState.ClosingTrade, runner.Snapshot.State);
        Assert.True(runner.IsActive); // Plugin must not enable its incoming auto-accept controller.
        Assert.Contains("MarketMafioso", suppression);
        Assert.False(runner.Start().Success);
        Assert.Equal(2, Assert.Single(queue).Quantity);

        clock.Advance(TimeSpan.FromSeconds(2));
        io.CancelEnabled = true;
        io.CancelClosesTrade = true;
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Stopped, runner.Snapshot.State);
        Assert.False(runner.IsActive);
        Assert.DoesNotContain("MarketMafioso", suppression);
        Assert.True(runner.HasResumeCheckpoint);
        Assert.Equal(2, io.CancelTradeAttempts);
    }
}
