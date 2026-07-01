using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class VIWIWorkshoppaIpcTests
{
    [Fact]
    public void SendQueue_LeavesQueueUntouchedWhenClearFails()
    {
        var ipc = new VIWIWorkshoppaIpc(new FakeAdapter(clearResult: false, addResult: true));
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
        };

        var result = ipc.SendQueue(queue, clearExisting: true);

        Assert.False(result.Success);
        Assert.Single(queue);
        Assert.Contains("clear", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendQueue_AddsEveryQueuedItem()
    {
        var adapter = new FakeAdapter(clearResult: true, addResult: true);
        var ipc = new VIWIWorkshoppaIpc(adapter);
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
            new() { WorkshopItemId = 1003, Quantity = 1 },
        };

        var result = ipc.SendQueue(queue, clearExisting: true);

        Assert.True(result.Success);
        Assert.Equal([(1002U, 2), (1003U, 1)], adapter.Added);
    }

    [Fact]
    public void SendQueue_FailsWhenAddFails()
    {
        var adapter = new FakeAdapter(clearResult: true, addResult: false);
        var ipc = new VIWIWorkshoppaIpc(adapter);
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
        };

        var result = ipc.SendQueue(queue, clearExisting: false);

        Assert.False(result.Success);
        Assert.Contains("add", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendQueue_FailsWhenAdapterUnavailable()
    {
        var ipc = new VIWIWorkshoppaIpc(new FakeAdapter(clearResult: true, addResult: true, isAvailable: false));
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
        };

        var result = ipc.SendQueue(queue, clearExisting: false);

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendQueue_SkipsInvalidItems()
    {
        var adapter = new FakeAdapter(clearResult: true, addResult: true);
        var ipc = new VIWIWorkshoppaIpc(adapter);
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 0, Quantity = 2 },
            new() { WorkshopItemId = 1002, Quantity = 0 },
            new() { WorkshopItemId = 1003, Quantity = -1 },
            new() { WorkshopItemId = 1004, Quantity = 3 },
        };

        var result = ipc.SendQueue(queue, clearExisting: false);

        Assert.True(result.Success);
        Assert.Equal([(1004U, 3)], adapter.Added);
    }

    private sealed class FakeAdapter : IVIWIWorkshoppaIpcAdapter
    {
        private readonly bool clearResult;
        private readonly bool addResult;
        private readonly bool isAvailable;

        public FakeAdapter(bool clearResult, bool addResult, bool isAvailable = true)
        {
            this.clearResult = clearResult;
            this.addResult = addResult;
            this.isAvailable = isAvailable;
        }

        public List<(uint WorkshopItemId, int Quantity)> Added { get; } = new();
        public bool IsAvailable => isAvailable;
        public bool ClearQueue() => clearResult;

        public bool AddQueueItem(uint workshopItemId, int quantity)
        {
            Added.Add((workshopItemId, quantity));
            return addResult;
        }
    }
}
