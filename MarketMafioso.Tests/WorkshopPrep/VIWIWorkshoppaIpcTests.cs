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
        Assert.Equal([(uint)1002, (uint)1003], adapter.Added.Select(x => x.WorkshopItemId).ToArray());
    }

    private sealed class FakeAdapter : IVIWIWorkshoppaIpcAdapter
    {
        private readonly bool clearResult;
        private readonly bool addResult;

        public FakeAdapter(bool clearResult, bool addResult)
        {
            this.clearResult = clearResult;
            this.addResult = addResult;
        }

        public List<(uint WorkshopItemId, int Quantity)> Added { get; } = new();
        public bool IsAvailable => true;
        public bool ClearQueue() => clearResult;

        public bool AddQueueItem(uint workshopItemId, int quantity)
        {
            Added.Add((workshopItemId, quantity));
            return addResult;
        }
    }
}
