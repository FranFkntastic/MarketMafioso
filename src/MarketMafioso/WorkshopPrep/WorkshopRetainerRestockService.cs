using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using MarketMafioso.Automation.Retainers;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopRetainerRestockState
{
    Idle,
    Planning,
    WaitingForRetainerList,
    OpeningRetainer,
    OpeningInventory,
    WithdrawingItems,
    DepositingItems,
    ClosingRetainer,
    Complete,
    Failed,
}

public sealed class WorkshopRetainerRestockService
{
    private readonly IPluginLog log;
    private readonly IWorkshopRetainerRestockDriver driver;
    private bool isRunning;
    private string lastStatus = "No retainer transfer has run.";

    public WorkshopRetainerRestockService(IPluginLog log)
        : this(log, new WorkshopRetainerRestockDriver(log))
    {
    }

    internal WorkshopRetainerRestockService(IPluginLog log, IWorkshopRetainerRestockDriver driver)
    {
        this.log = log;
        this.driver = driver;
    }

    public bool IsRunning => isRunning;
    public string LastStatus => lastStatus;
    public WorkshopRetainerRestockState State { get; private set; } = WorkshopRetainerRestockState.Idle;

    public async Task StartRestockAsync(IReadOnlyList<RetainerRestockPlanLine> planLines)
    {
        if (isRunning)
        {
            lastStatus = "Retainer restock is already running.";
            return;
        }

        var request = BuildRestockRunRequest(planLines);
        if (request.RemainingQuantities.Count == 0)
        {
            lastStatus = "No restock quantities are needed.";
            return;
        }

        if (request.CandidateRetainers.Count == 0)
        {
            lastStatus = "No cached retainer candidates are available for the restock plan.";
            return;
        }

        await StartRunAsync(
            request.RemainingQuantities.ToDictionary(x => x.Key, x => x.Value),
            request.CandidateRetainers,
            static (remaining, totalRetrieved) =>
            {
                var summary = RetainerRestockCompletionSummary.Build(remaining, totalRetrieved);
                return new RestockRunCompletion(summary.IsSuccess, summary.Message);
            },
            "Retainer restock").ConfigureAwait(false);
    }

    public async Task StartAsync(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        if (isRunning)
        {
            lastStatus = "Workshop material restock is already running.";
            return;
        }

        var shortages = availability.Where(x => x.Shortage > 0).ToList();
        if (shortages.Count == 0)
        {
            lastStatus = "No workshop material shortages to restock.";
            return;
        }

        var remaining = shortages.ToDictionary(x => x.ItemId, x => x.Shortage);
        var candidates = shortages.SelectMany(x => x.CandidateRetainers)
            .DistinctBy(x => x.RetainerId)
            .ToList();
        if (candidates.Count == 0)
        {
            lastStatus = "No cached retainer candidates are available for the workshop material shortages.";
            return;
        }

        await StartRunAsync(
            remaining,
            candidates,
            static (remainingQuantities, totalRetrieved) =>
            {
                var summary = BuildCompletionSummary(remainingQuantities, totalRetrieved);
                return new RestockRunCompletion(summary.IsSuccess, summary.Message);
            },
            "Workshop material restock").ConfigureAwait(false);
    }

    public async Task StartElementalDepositAsync(ElementalDepositPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (isRunning)
        {
            lastStatus = "A retainer transfer is already running.";
            return;
        }

        if (!plan.CanRun)
        {
            lastStatus = plan.PlayerQuantity == 0
                ? "No elemental shards or crystals are currently carried."
                : "No cached retainer crystal capacity is available. Refresh the retainer cache first.";
            return;
        }

        var remaining = plan.Lines.ToDictionary(line => line.ItemId, line => line.PlayerQuantity);
        var candidates = plan.Candidates.Select(candidate => new RetainerMaterialCandidate(
            candidate.RetainerId,
            candidate.RetainerName,
            candidate.LastUpdatedUtc,
            candidate.UsableCapacity)).ToList();

        isRunning = true;
        State = WorkshopRetainerRestockState.Planning;
        var retainerOpen = false;
        try
        {
            var totalDeposited = 0;
            State = WorkshopRetainerRestockState.WaitingForRetainerList;
            await driver.WaitForRetainerListAsync().ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                State = WorkshopRetainerRestockState.OpeningRetainer;
                await driver.OpenRetainerAsync(candidate).ConfigureAwait(false);
                retainerOpen = true;

                State = WorkshopRetainerRestockState.OpeningInventory;
                await driver.OpenRetainerInventoryAsync().ConfigureAwait(false);

                State = WorkshopRetainerRestockState.DepositingItems;
                totalDeposited += await DepositIntoOpenRetainerAsync(remaining).ConfigureAwait(false);

                State = WorkshopRetainerRestockState.ClosingRetainer;
                await driver.CloseRetainerAsync().ConfigureAwait(false);
                retainerOpen = false;

                if (remaining.Values.All(quantity => quantity <= 0))
                    break;
            }

            var remainingTotal = remaining.Values.Where(quantity => quantity > 0).Sum();
            if (totalDeposited == 0)
                throw new InvalidOperationException("No live retainer crystal capacity was available.");

            State = WorkshopRetainerRestockState.Complete;
            lastStatus = remainingTotal == 0
                ? $"Quick deposit complete. Deposited {totalDeposited:N0} elemental shard/crystal units."
                : $"Quick deposit partially complete. Deposited {totalDeposited:N0}; {remainingTotal:N0} remain on the character.";
            log.Information($"[MarketMafioso] {lastStatus}");
        }
        catch (Exception ex)
        {
            var failedState = State;
            if (retainerOpen)
            {
                try
                {
                    await driver.CloseRetainerAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    log.Warning(cleanupException, "[MarketMafioso] Unable to close the retainer after quick deposit failed.");
                }
            }

            State = WorkshopRetainerRestockState.Failed;
            lastStatus = $"Quick deposit failed during {failedState}. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Quick deposit failed.");
        }
        finally
        {
            isRunning = false;
        }
    }

    public static RetainerRestockRunRequest BuildRestockRunRequest(IReadOnlyList<RetainerRestockPlanLine> planLines)
    {
        ArgumentNullException.ThrowIfNull(planLines);

        var remaining = planLines
            .Where(line => line.NeededQuantity > 0)
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.NeededQuantity));
        var candidates = planLines
            .Where(line => line.NeededQuantity > 0)
            .SelectMany(line => line.Candidates)
            .GroupBy(candidate => candidate.RetainerId)
            .Select(group =>
            {
                var first = group.First();
                return new RetainerMaterialCandidate(
                    first.RetainerId,
                    first.RetainerName,
                    first.LastUpdatedUtc,
                    group.Sum(candidate => candidate.CachedQuantity));
            })
            .OrderByDescending(candidate => candidate.Quantity)
            .ThenByDescending(candidate => candidate.LastUpdated)
            .ThenBy(candidate => candidate.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RetainerRestockRunRequest(remaining, candidates);
    }

    private async Task StartRunAsync(
        Dictionary<uint, int> remaining,
        IReadOnlyList<RetainerMaterialCandidate> candidates,
        Func<IReadOnlyDictionary<uint, int>, int, RestockRunCompletion> buildCompletion,
        string runLabel)
    {
        isRunning = true;
        State = WorkshopRetainerRestockState.Planning;
        var retainerOpen = false;
        try
        {
            var totalRetrieved = 0;
            State = WorkshopRetainerRestockState.WaitingForRetainerList;
            await driver.WaitForRetainerListAsync().ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                State = WorkshopRetainerRestockState.OpeningRetainer;
                await driver.OpenRetainerAsync(candidate).ConfigureAwait(false);
                retainerOpen = true;

                State = WorkshopRetainerRestockState.OpeningInventory;
                await driver.OpenRetainerInventoryAsync().ConfigureAwait(false);

                State = WorkshopRetainerRestockState.WithdrawingItems;
                var retrievedFromCandidate = await WithdrawFromOpenRetainerAsync(remaining).ConfigureAwait(false);
                totalRetrieved += retrievedFromCandidate;
                if (retrievedFromCandidate == 0)
                    log.Information($"[MarketMafioso] No matching live retainer stacks were found for candidate {candidate.RetainerName}.");

                State = WorkshopRetainerRestockState.ClosingRetainer;
                await driver.CloseRetainerAsync().ConfigureAwait(false);
                retainerOpen = false;

                if (remaining.Values.All(x => x <= 0))
                    break;
            }

            var summary = buildCompletion(remaining, totalRetrieved);
            if (!summary.IsSuccess)
            {
                State = WorkshopRetainerRestockState.WithdrawingItems;
                throw new InvalidOperationException(summary.Message);
            }

            State = WorkshopRetainerRestockState.Complete;
            lastStatus = summary.Message;
            log.Information($"[MarketMafioso] {summary.Message}");
        }
        catch (Exception ex)
        {
            var failedState = State;
            if (retainerOpen)
            {
                try
                {
                    await driver.CloseRetainerAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    log.Warning(cleanupException, $"[MarketMafioso] Unable to close the retainer after {runLabel} failed.");
                }
            }

            State = WorkshopRetainerRestockState.Failed;
            lastStatus = $"{runLabel} failed during {failedState}. {ex.Message}";
            log.Error(ex, $"[MarketMafioso] {runLabel} failed.");
        }
        finally
        {
            isRunning = false;
            if (State is not WorkshopRetainerRestockState.Complete and not WorkshopRetainerRestockState.Failed)
                State = WorkshopRetainerRestockState.Idle;
        }
    }

    public IReadOnlyList<DalamudInventoryStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds)
    {
        return driver.ScanLiveRetainerStacks(itemIds);
    }

    public static WorkshopRetainerRestockCompletionSummary BuildCompletionSummary(
        IReadOnlyDictionary<uint, int> remaining,
        int totalRetrieved)
    {
        var remainingText = FormatRemainingShortages(remaining);
        if (string.IsNullOrEmpty(remainingText))
            return new(true, false, $"Workshop material restock complete. Retrieved {totalRetrieved} item(s).");

        if (totalRetrieved > 0)
            return new(true, true, $"Workshop material restock partially complete. Retrieved {totalRetrieved} item(s); remaining shortages: {remainingText}.");

        return new(false, false, $"No matching live retainer stacks were found for the workshop material shortages: {remainingText}.");
    }

    internal static string? GetAutomatedRestockStartError(bool isRetainerInventoryReady) =>
        isRetainerInventoryReady
            ? "Close the current retainer inventory before starting automated workshop material restock."
            : null;

    private async Task<int> WithdrawFromOpenRetainerAsync(Dictionary<uint, int> remaining)
    {
        var retrievedTotal = 0;
        var plannedStacks = new HashSet<DalamudInventoryStack>();
        var itemIds = remaining.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet();
        var liveStacks = await driver.ScanLiveRetainerStacksAsync(itemIds).ConfigureAwait(false);
        foreach (var stack in liveStacks)
        {
            if (plannedStacks.Contains(stack))
                continue;

            if (!remaining.TryGetValue(stack.ItemId, out var needed) || needed <= 0)
                continue;

            var quantity = Math.Min(needed, stack.Quantity);
            var result = await driver.RetrieveFromLiveStackAsync(stack, quantity).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(result.Message);

            remaining[stack.ItemId] -= result.Retrieved;
            retrievedTotal += result.Retrieved;
            plannedStacks.Add(stack);
            log.Information($"[MarketMafioso] Retrieved {result.Retrieved}x item {stack.ItemId} from {stack.Container}/{stack.SlotIndex}.");
        }

        return retrievedTotal;
    }

    private async Task<int> DepositIntoOpenRetainerAsync(Dictionary<uint, int> remaining)
    {
        var depositedTotal = 0;
        var itemIds = remaining.Where(entry => entry.Value > 0).Select(entry => entry.Key).ToHashSet();
        var liveStacks = await driver.ScanLivePlayerCrystalStacksAsync(itemIds).ConfigureAwait(false);
        foreach (var stack in liveStacks)
        {
            if (!remaining.TryGetValue(stack.ItemId, out var quantity) || quantity <= 0)
                continue;

            var result = await driver.DepositCrystalStackAsync(stack, Math.Min(quantity, stack.Quantity)).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(result.Message);

            remaining[stack.ItemId] -= result.Transferred;
            depositedTotal += result.Transferred;
            log.Information($"[MarketMafioso] Deposited {result.Transferred}x item {stack.ItemId} from crystal slot {stack.SlotIndex}.");
        }

        return depositedTotal;
    }

    private static string FormatRemainingShortages(IReadOnlyDictionary<uint, int> remaining)
    {
        return string.Join(
            ", ",
            remaining
                .Where(x => x.Value > 0)
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value}"));
    }
}

public sealed record RetainerRetrievalResult(
    bool Success,
    int Retrieved,
    string Message);

public sealed record WorkshopRetainerRestockCompletionSummary(
    bool IsSuccess,
    bool IsPartial,
    string Message);

public sealed record RetainerRestockRunRequest(
    IReadOnlyDictionary<uint, int> RemainingQuantities,
    IReadOnlyList<RetainerMaterialCandidate> CandidateRetainers);

internal sealed record RestockRunCompletion(
    bool IsSuccess,
    string Message);
