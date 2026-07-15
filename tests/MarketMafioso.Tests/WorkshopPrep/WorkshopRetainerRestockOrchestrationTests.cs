using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.RetainerRestock;
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

    [Fact]
    public async Task StartElementalDepositAsync_UsesRetainersUntilAllCarriedCrystalsAreDeposited()
    {
        var driver = new FakeWorkshopRetainerRestockDriver
        {
            PlayerCrystals = [new DalamudInventoryStack(InventoryType.Crystals, 0, 2, 1_500)],
        };
        driver.DepositCapacityByRetainer["Alpha"] = 500;
        driver.DepositCapacityByRetainer["Beta"] = 1_000;
        var service = CreateService(driver);
        var plan = new ElementalDepositPlan(
            DateTime.UtcNow,
            [new ElementalDepositPlanLine(2, "Fire Shard", 1_500, 1_500, 1_500, 0)],
            [
                DepositCandidate(1, "Alpha", 500),
                DepositCandidate(2, "Beta", 1_000),
            ],
            2,
            0);

        await service.StartElementalDepositAsync(plan);

        Assert.Contains("deposit:Alpha:2:500", driver.Calls);
        Assert.Contains("deposit:Beta:2:1000", driver.Calls);
        Assert.Equal(WorkshopRetainerRestockState.Complete, service.State);
        Assert.Equal("Quick deposit complete. Deposited 1,500 elemental shard/crystal units.", service.LastStatus);
    }

    [Fact]
    public async Task StartElementalDepositAsync_ContinuesPastRetainersWithNoLiveCapacity()
    {
        var driver = new FakeWorkshopRetainerRestockDriver
        {
            PlayerCrystals = [new DalamudInventoryStack(InventoryType.Crystals, 0, 2, 100)],
        };
        driver.DepositCapacityByRetainer["Full one"] = 0;
        driver.DepositCapacityByRetainer["Full two"] = 0;
        driver.DepositCapacityByRetainer["Available"] = 100;
        var service = CreateService(driver);
        var plan = new ElementalDepositPlan(
            DateTime.UtcNow,
            [new ElementalDepositPlanLine(2, "Fire Shard", 100, 29_997, 100, 0)],
            [
                DepositCandidate(1, "Full one", 9_999),
                DepositCandidate(2, "Full two", 9_999),
                DepositCandidate(3, "Available", 9_999),
            ],
            3,
            3);

        await service.StartElementalDepositAsync(plan);

        Assert.Contains("deposit:Full one:2:0", driver.Calls);
        Assert.Contains("deposit:Full two:2:0", driver.Calls);
        Assert.Contains("deposit:Available:2:100", driver.Calls);
        Assert.Equal(WorkshopRetainerRestockState.Complete, service.State);
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

    private static ElementalDepositRetainerCandidate DepositCandidate(ulong id, string name, int capacity) =>
        new(id, name, DateTime.UnixEpoch, new Dictionary<uint, int> { [2] = capacity }, capacity, true);

    private static DalamudInventoryStack Stack(uint itemId, int quantity, int slotIndex) =>
        new(InventoryType.RetainerPage1, slotIndex, itemId, quantity);

    private sealed class FakeWorkshopRetainerRestockDriver : IWorkshopRetainerRestockDriver
    {
        private string? openRetainer;

        public Dictionary<string, IReadOnlyList<DalamudInventoryStack>> StacksByRetainer { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? WaitForRetainerListException { get; init; }
        public Exception? RetrieveException { get; init; }
        public List<DalamudInventoryStack> PlayerCrystals { get; init; } = [];
        public Dictionary<string, int> DepositCapacityByRetainer { get; } = [];

        public IReadOnlyList<DalamudInventoryStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds) =>
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

        public Task<IReadOnlyList<DalamudInventoryStack>> ScanLiveRetainerStacksAsync(IReadOnlySet<uint> itemIds)
        {
            Calls.Add($"scan:{openRetainer}:{string.Join(',', itemIds.OrderBy(x => x))}");
            return Task.FromResult(GetCurrentStacks(itemIds));
        }

        public Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(DalamudInventoryStack stack, int quantity)
        {
            Calls.Add($"retrieve:{openRetainer}:{stack.ItemId}:{quantity}");
            return RetrieveException == null
                ? Task.FromResult(new RetainerRetrievalResult(true, quantity, "Retrieved."))
                : Task.FromException<RetainerRetrievalResult>(RetrieveException);
        }

        public Task<IReadOnlyList<DalamudInventoryStack>> ScanLivePlayerCrystalStacksAsync(IReadOnlySet<uint> itemIds)
        {
            Calls.Add($"scan-player-crystals:{openRetainer}:{string.Join(',', itemIds.OrderBy(x => x))}");
            return Task.FromResult<IReadOnlyList<DalamudInventoryStack>>(
                PlayerCrystals.Where(stack => itemIds.Contains(stack.ItemId) && stack.Quantity > 0).ToList());
        }

        public Task<RetainerCrystalTransferResult> DepositCrystalStackAsync(DalamudInventoryStack stack, int quantity)
        {
            var capacity = openRetainer != null && DepositCapacityByRetainer.TryGetValue(openRetainer, out var available)
                ? available
                : 0;
            var deposited = Math.Min(quantity, capacity);
            if (openRetainer != null)
                DepositCapacityByRetainer[openRetainer] = capacity - deposited;
            var index = PlayerCrystals.IndexOf(stack);
            if (index >= 0)
                PlayerCrystals[index] = stack with { Quantity = stack.Quantity - deposited };
            Calls.Add($"deposit:{openRetainer}:{stack.ItemId}:{deposited}");
            return Task.FromResult(new RetainerCrystalTransferResult(
                true,
                deposited,
                "TransferVerified",
                "Deposited."));
        }

        public Task CloseRetainerAsync()
        {
            Calls.Add($"close:{openRetainer}");
            openRetainer = null;
            return Task.CompletedTask;
        }

        private IReadOnlyList<DalamudInventoryStack> GetCurrentStacks(IReadOnlySet<uint> itemIds) =>
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
