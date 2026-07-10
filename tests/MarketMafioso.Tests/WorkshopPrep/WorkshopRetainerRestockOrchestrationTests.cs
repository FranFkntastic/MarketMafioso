using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Retainers;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopRetainerRestockOrchestrationTests
{
    [Fact]
    public async Task StartAsync_SequencesCandidatesAndRequestsOnlyRemainingQuantities()
    {
        var driver = new FakeWorkshopRetainerRestockDriver();
        driver.StacksByRetainer["Alpha"] =
        [
            Stack(100, quantity: 4, slotIndex: 0),
            Stack(200, quantity: 10, slotIndex: 1),
        ];
        driver.StacksByRetainer["Beta"] =
        [
            Stack(100, quantity: 10, slotIndex: 0),
        ];
        var service = CreateService(driver);

        await service.StartAsync(
        [
            Availability(100, shortage: 6, Candidate(1, "Alpha"), Candidate(2, "Beta")),
            Availability(200, shortage: 4, Candidate(1, "Alpha")),
        ]);

        Assert.Equal(
        [
            "wait-list",
            "open:Alpha",
            "open-inventory:Alpha",
            "scan:Alpha:100,200",
            "retrieve:Alpha:100:4",
            "retrieve:Alpha:200:4",
            "close:Alpha",
            "open:Beta",
            "open-inventory:Beta",
            "scan:Beta:100",
            "retrieve:Beta:100:2",
            "close:Beta",
        ],
        driver.Calls);
        Assert.Equal(WorkshopRetainerRestockState.Complete, service.State);
        Assert.False(service.IsRunning);
        Assert.Equal("Workshop material restock complete. Retrieved 10 item(s).", service.LastStatus);
    }

    [Fact]
    public async Task StartAsync_StopsAfterTheCurrentCandidateFillsAllShortages()
    {
        var driver = new FakeWorkshopRetainerRestockDriver();
        driver.StacksByRetainer["Alpha"] = [Stack(100, quantity: 8, slotIndex: 0)];
        var service = CreateService(driver);

        await service.StartAsync(
        [
            Availability(100, shortage: 5, Candidate(1, "Alpha"), Candidate(2, "Beta")),
        ]);

        Assert.DoesNotContain("open:Beta", driver.Calls);
        Assert.Contains("retrieve:Alpha:100:5", driver.Calls);
        Assert.Equal("close:Alpha", driver.Calls[^1]);
        Assert.Equal(WorkshopRetainerRestockState.Complete, service.State);
    }

    [Fact]
    public async Task StartAsync_PreservesPartialCompletionPolicyInTheService()
    {
        var driver = new FakeWorkshopRetainerRestockDriver();
        driver.StacksByRetainer["Alpha"] = [Stack(100, quantity: 3, slotIndex: 0)];
        var service = CreateService(driver);

        await service.StartAsync(
        [
            Availability(100, shortage: 5, Candidate(1, "Alpha")),
        ]);

        Assert.Equal(WorkshopRetainerRestockState.Complete, service.State);
        Assert.Equal(
            "Workshop material restock partially complete. Retrieved 3 item(s); remaining shortages: 100:2.",
            service.LastStatus);
    }

    [Fact]
    public async Task StartAsync_HandlesDriverCancellationAsRunFailureAndClearsRunningState()
    {
        var driver = new FakeWorkshopRetainerRestockDriver
        {
            WaitForRetainerListException = new OperationCanceledException("Framework is unloading."),
        };
        var service = CreateService(driver);

        await service.StartAsync(
        [
            Availability(100, shortage: 5, Candidate(1, "Alpha")),
        ]);

        Assert.Equal(WorkshopRetainerRestockState.Failed, service.State);
        Assert.False(service.IsRunning);
        Assert.Equal(
            "Workshop material restock failed during WaitingForRetainerList. Framework is unloading.",
            service.LastStatus);
    }

    [Fact]
    public async Task StartAsync_ClosesOpenRetainerAfterRetrievalFailure()
    {
        var driver = new FakeWorkshopRetainerRestockDriver
        {
            RetrieveException = new InvalidOperationException("Retrieval failed."),
        };
        driver.StacksByRetainer["Alpha"] = [Stack(100, quantity: 5, slotIndex: 0)];
        var service = CreateService(driver);

        await service.StartAsync(
        [
            Availability(100, shortage: 5, Candidate(1, "Alpha")),
        ]);

        Assert.Equal(WorkshopRetainerRestockState.Failed, service.State);
        Assert.Equal("close:Alpha", driver.Calls[^1]);
        Assert.Equal(
            "Workshop material restock failed during WithdrawingItems. Retrieval failed.",
            service.LastStatus);
    }

    private static WorkshopRetainerRestockService CreateService(IWorkshopRetainerRestockDriver driver) =>
        new(TestPluginLog.Create(), driver);

    private static WorkshopMaterialAvailability Availability(
        uint itemId,
        int shortage,
        params RetainerMaterialCandidate[] candidates) =>
        new(itemId, $"Item {itemId}", 0, shortage, 0, shortage, shortage, shortage, candidates);

    private static RetainerMaterialCandidate Candidate(ulong id, string name) =>
        new(id, name, DateTime.UnixEpoch, 99);

    private static LiveRetainerStack Stack(uint itemId, int quantity, int slotIndex) =>
        (LiveRetainerStack)typeof(LiveRetainerStack).GetConstructors().Single().Invoke(
        [
            Enum.ToObject(typeof(LiveRetainerStack).GetProperty("Page")!.PropertyType, 0),
            slotIndex,
            itemId,
            quantity,
        ]);

    private sealed class FakeWorkshopRetainerRestockDriver : IWorkshopRetainerRestockDriver
    {
        private string? openRetainer;

        public Dictionary<string, IReadOnlyList<LiveRetainerStack>> StacksByRetainer { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? WaitForRetainerListException { get; init; }
        public Exception? RetrieveException { get; init; }

        public IReadOnlyList<LiveRetainerStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds) =>
            GetCurrentStacks(itemIds);

        public Task WaitForRetainerListAsync()
        {
            Calls.Add("wait-list");
            return WaitForRetainerListException == null
                ? Task.CompletedTask
                : Task.FromException(WaitForRetainerListException);
        }

        public Task OpenRetainerAsync(RetainerMaterialCandidate candidate)
        {
            openRetainer = candidate.RetainerName;
            Calls.Add($"open:{openRetainer}");
            return Task.CompletedTask;
        }

        public Task OpenRetainerInventoryAsync()
        {
            Calls.Add($"open-inventory:{openRetainer}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LiveRetainerStack>> ScanLiveRetainerStacksAsync(IReadOnlySet<uint> itemIds)
        {
            Calls.Add($"scan:{openRetainer}:{string.Join(',', itemIds.OrderBy(x => x))}");
            return Task.FromResult(GetCurrentStacks(itemIds));
        }

        public Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(LiveRetainerStack stack, int quantity)
        {
            Calls.Add($"retrieve:{openRetainer}:{stack.ItemId}:{quantity}");
            return RetrieveException == null
                ? Task.FromResult(new RetainerRetrievalResult(true, quantity, "Retrieved."))
                : Task.FromException<RetainerRetrievalResult>(RetrieveException);
        }

        public Task CloseRetainerAsync()
        {
            Calls.Add($"close:{openRetainer}");
            openRetainer = null;
            return Task.CompletedTask;
        }

        private IReadOnlyList<LiveRetainerStack> GetCurrentStacks(IReadOnlySet<uint> itemIds) =>
            openRetainer != null && StacksByRetainer.TryGetValue(openRetainer, out var stacks)
                ? stacks.Where(stack => itemIds.Contains(stack.ItemId)).ToList()
                : [];
    }

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType != typeof(void) && targetMethod?.ReturnType?.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}
