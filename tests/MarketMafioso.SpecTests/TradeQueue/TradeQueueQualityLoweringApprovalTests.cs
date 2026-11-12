using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed partial class TradeQueueRunnerTests
{
    private static readonly TradeQueuePartner Recipient = new(1, "Recipient", 2, "Siren");

    private static IReadOnlyList<TradeQueueInventoryStack> HqInventory(int quantity, int slot = 3) =>
    [
        new(0, slot, 100, "Cobalt Ingot", true, quantity),
    ];

    [Fact]
    public void Preview_ReconcilesQueueAndReportsWholeStackOverConversion()
    {
        var preview = TradeQueueQualityLoweringPreview.Build(Queue(2), HqInventory(10));

        Assert.True(preview.Success);
        var step = Assert.Single(preview.Steps);
        Assert.Equal(10, step.ConvertedUnits);
        Assert.Equal(2, step.RequiredUnits);
        Assert.Equal(8, preview.ExcessConvertedUnits);
        Assert.Equal("0:3:100:10", preview.Signature);
        Assert.Contains("8 beyond the queue", preview.Describe());

        // A queue larger than stock is clamped exactly as Start clamps it, without mutating the caller's queue.
        var queue = Queue(5);
        var clamped = TradeQueueQualityLoweringPreview.Build(queue, HqInventory(3));
        Assert.Equal(3, Assert.Single(clamped.Steps).RequiredUnits);
        Assert.Equal(5, Assert.Single(queue).Quantity);
    }

    [Fact]
    public void Preview_IsEmptyWhenNormalQualityCoversTheQueue()
    {
        var preview = TradeQueueQualityLoweringPreview.Build(Queue(2), Inventory(2));

        Assert.True(preview.Success);
        Assert.False(preview.RequiresLowering);
        Assert.Equal(string.Empty, preview.Signature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_RefusesToLowerQualityWithoutApproval(bool exactRecipient)
    {
        var queue = Queue(2);
        var io = new FakeIo(HqInventory(10));
        var quality = new FakeQualityLowering();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(queue, new(), () => { }, io, quality, coordinator, TestPluginLog.Create());

        var result = exactRecipient ? runner.Start(Recipient) : runner.Start();

        Assert.False(result.Success);
        Assert.StartsWith("Confirm the HQ-to-NQ conversion", result.Message);
        Assert.NotNull(result.QualityLoweringApprovalRequired);
        Assert.Same(result.QualityLoweringApprovalRequired, runner.PendingQualityLoweringApproval);
        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.False(runner.IsActive);
        Assert.Equal(0, quality.BeginCount);
        Assert.Equal(0, io.OpenTradeAttempts);
        Assert.Equal(2, Assert.Single(queue).Quantity);
    }

    [Fact]
    public void Start_LowersQualityWhenApprovalMatchesTheCurrentPlan()
    {
        var io = new FakeIo(HqInventory(10));
        var quality = new FakeQualityLowering();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io, quality, coordinator, TestPluginLog.Create());
        var approval = runner.Start(Recipient).QualityLoweringApprovalRequired!;

        var result = runner.Start(Recipient, new(ApprovedQualityLoweringSignature: approval.Signature));

        Assert.True(result.Success, result.Message);
        Assert.Null(runner.PendingQualityLoweringApproval);
        Assert.Equal(TradeQueueExecutionState.NormalizingQuality, runner.Snapshot.State);
        Assert.Equal(1, quality.BeginCount);
    }

    [Fact]
    public void Start_RejectsApprovalForAPlanThatNoLongerMatchesInventory()
    {
        var io = new FakeIo(HqInventory(10));
        var quality = new FakeQualityLowering();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io, quality, coordinator, TestPluginLog.Create());
        var approval = runner.Start(Recipient).QualityLoweringApprovalRequired!;

        // The stack moved and grew between the dialog and the click: the same item, a different conversion.
        io.Inventory = HqInventory(40, slot: 7);
        var result = runner.Start(Recipient, new(ApprovedQualityLoweringSignature: approval.Signature));

        Assert.False(result.Success);
        Assert.Contains("changed since it was confirmed", result.Message);
        Assert.Equal("0:7:100:40", result.QualityLoweringApprovalRequired!.Signature);
        Assert.Equal(0, quality.BeginCount);
        Assert.Equal(0, io.OpenTradeAttempts);
    }

    [Fact]
    public void Start_PreserveHighQualityAppliesToThisRunOnly()
    {
        var io = new FakeIo(HqInventory(10));
        var quality = new FakeQualityLowering();
        var policy = new TradeQueuePolicyOptions { NormalizeHighQualityItems = true };
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            Queue(2), new(), () => { }, io, quality, coordinator, TestPluginLog.Create(), clock.Read, policy);

        var result = runner.Start(Recipient, new(NormalizeHighQualityItems: false));

        Assert.True(result.Success, result.Message);
        Assert.Equal(TradeQueueExecutionState.OpeningTrade, runner.Snapshot.State);
        Assert.Equal(0, quality.BeginCount);
        Assert.True(policy.NormalizeHighQualityItems);

        runner.Stop();
        var next = runner.Start(Recipient);
        Assert.False(next.Success);
        Assert.NotNull(next.QualityLoweringApprovalRequired);
        Assert.Equal(0, quality.BeginCount);
    }

    [Fact]
    public void DeferredStart_RefusesUnapprovedLoweringOnceInventoryBecomesAuthoritative()
    {
        var io = new FakeIo(HqInventory(10)) { InventoryIsAuthoritative = false };
        var quality = new FakeQualityLowering();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(Queue(2), new(), () => { }, io, quality, coordinator, TestPluginLog.Create());

        Assert.True(runner.Start(Recipient).Success);
        Assert.Equal(TradeQueueExecutionState.PreparingInventory, runner.Snapshot.State);
        Assert.Null(runner.PendingQualityLoweringApproval);

        io.InventoryIsAuthoritative = true;
        runner.Tick();

        Assert.False(runner.IsActive);
        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.NotNull(runner.PendingQualityLoweringApproval);
        Assert.Equal(0, quality.BeginCount);
        Assert.Equal(0, io.OpenTradeAttempts);
    }

    [Fact]
    public void Normalizing_FailsWithTheStalledGateWhenAutomationStopsProgressing()
    {
        const string gate = "Waiting until the game allows lowering item quality.";
        var io = new FakeIo(Inventory(2));
        var clock = new TestClock();
        using var coordinator = Coordinator(new());
        using var runner = new TradeQueueRunner(
            Queue(2), new(), () => { }, io, new FakeQualityLowering(stalledAt: gate), coordinator,
            TestPluginLog.Create(), clock.Read);

        Assert.True(runner.Start(Recipient).Success);
        clock.Advance(TimeSpan.FromSeconds(30));
        runner.Tick();
        Assert.Equal(TradeQueueExecutionState.NormalizingQuality, runner.Snapshot.State);
        Assert.Equal(gate, runner.Snapshot.Message);

        clock.Advance(TimeSpan.FromSeconds(20));
        runner.Tick();

        Assert.Equal(TradeQueueExecutionState.Failed, runner.Snapshot.State);
        Assert.Contains("made no progress", runner.Snapshot.Message);
        Assert.Contains($"Last observation: {gate}", runner.Snapshot.Message);
        Assert.False(runner.IsActive);
        Assert.Equal(0, io.OpenTradeAttempts);
    }
}
