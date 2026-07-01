using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopQueueOperationResult(bool Success, string Message, Guid? QueueId = null);

public enum WorkshopFrozenQueueState
{
    NotLoaded,
    Loaded,
    Modified,
}

public static class WorkshopQueueService
{
    public static WorkshopQueueOperationResult FreezeCurrentQueue(Configuration config, string name, DateTime nowUtc)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return new(false, "Frozen queue name is required.");

        if (config.WorkshopPrepQueue.Count == 0)
            return new(false, "Active workshop queue is empty.");

        if (HasDuplicateName(config, normalizedName, exceptId: null))
            return new(false, $"A frozen queue named {normalizedName} already exists.");

        var frozen = new WorkshopFrozenQueue
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            Items = CloneItems(config.WorkshopPrepQueue),
        };

        config.FrozenWorkshopQueues.Add(frozen);
        config.ActiveFrozenWorkshopQueueId = frozen.Id;
        return new(true, $"Froze workshop queue {normalizedName}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult SaveActiveQueue(Configuration config, string name, DateTime nowUtc)
    {
        if (config.ActiveFrozenWorkshopQueueId is { } queueId)
            return OverwriteFrozenQueue(config, queueId, nowUtc);

        return FreezeCurrentQueue(config, name, nowUtc);
    }

    public static WorkshopQueueOperationResult LoadFrozenQueue(Configuration config, Guid queueId)
    {
        var frozen = FindFrozenQueue(config, queueId);
        if (frozen == null)
            return new(false, "Frozen workshop queue was not found.");

        config.WorkshopPrepQueue = CloneItems(frozen.Items);
        config.ActiveFrozenWorkshopQueueId = frozen.Id;
        return new(true, $"Loaded frozen workshop queue {frozen.Name}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult OverwriteFrozenQueue(Configuration config, Guid queueId, DateTime nowUtc)
    {
        var frozen = FindFrozenQueue(config, queueId);
        if (frozen == null)
            return new(false, "Frozen workshop queue was not found.");

        if (config.WorkshopPrepQueue.Count == 0)
            return new(false, "Active workshop queue is empty.");

        frozen.Items = CloneItems(config.WorkshopPrepQueue);
        frozen.UpdatedAt = nowUtc;
        config.ActiveFrozenWorkshopQueueId = frozen.Id;
        return new(true, $"Updated frozen workshop queue {frozen.Name}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult RenameFrozenQueue(Configuration config, Guid queueId, string name, DateTime nowUtc)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return new(false, "Frozen queue name is required.");

        var frozen = FindFrozenQueue(config, queueId);
        if (frozen == null)
            return new(false, "Frozen workshop queue was not found.");

        if (HasDuplicateName(config, normalizedName, queueId))
            return new(false, $"A frozen queue named {normalizedName} already exists.");

        frozen.Name = normalizedName;
        frozen.UpdatedAt = nowUtc;
        return new(true, $"Renamed frozen workshop queue to {normalizedName}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult DuplicateFrozenQueue(Configuration config, Guid queueId, string name, DateTime nowUtc)
    {
        var source = FindFrozenQueue(config, queueId);
        if (source == null)
            return new(false, "Frozen workshop queue was not found.");

        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return new(false, "Frozen queue name is required.");

        if (HasDuplicateName(config, normalizedName, exceptId: null))
            return new(false, $"A frozen queue named {normalizedName} already exists.");

        var frozen = new WorkshopFrozenQueue
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            Items = CloneItems(source.Items),
        };

        config.FrozenWorkshopQueues.Add(frozen);
        return new(true, $"Duplicated frozen workshop queue {source.Name}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult DeleteFrozenQueue(Configuration config, Guid queueId)
    {
        var frozen = FindFrozenQueue(config, queueId);
        if (frozen == null)
            return new(false, "Frozen workshop queue was not found.");

        config.FrozenWorkshopQueues.Remove(frozen);
        if (config.ActiveFrozenWorkshopQueueId == queueId)
            config.ActiveFrozenWorkshopQueueId = null;

        return new(true, $"Deleted frozen workshop queue {frozen.Name}.");
    }

    public static void NewActiveQueue(Configuration config)
    {
        config.WorkshopPrepQueue.Clear();
        config.ActiveFrozenWorkshopQueueId = null;
    }

    public static WorkshopQueueOperationResult DecrementActiveQueue(Configuration config, uint workshopItemId)
    {
        var item = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
        if (item == null)
            return new(false, $"Active workshop queue does not contain project {workshopItemId}.");

        item.Quantity--;
        if (item.Quantity <= 0)
            config.WorkshopPrepQueue.Remove(item);

        MarkActiveQueueEdited(config);
        return new(true, "Decremented active workshop queue.");
    }

    public static bool ActiveQueueMatchesFrozenQueue(Configuration config)
    {
        if (config.ActiveFrozenWorkshopQueueId == null)
            return false;

        var frozen = FindFrozenQueue(config, config.ActiveFrozenWorkshopQueueId.Value);
        return frozen != null && ItemsEqual(config.WorkshopPrepQueue, frozen.Items);
    }

    public static WorkshopFrozenQueueState GetFrozenQueueState(Configuration config, Guid queueId)
    {
        if (config.ActiveFrozenWorkshopQueueId != queueId)
            return WorkshopFrozenQueueState.NotLoaded;

        var frozen = FindFrozenQueue(config, queueId);
        if (frozen == null)
            return WorkshopFrozenQueueState.NotLoaded;

        return ItemsEqual(config.WorkshopPrepQueue, frozen.Items)
            ? WorkshopFrozenQueueState.Loaded
            : WorkshopFrozenQueueState.Modified;
    }

    public static void MarkActiveQueueEdited(Configuration config)
    {
        // Keep the active frozen queue link so Save Queue can overwrite it after local edits.
    }

    public static List<WorkshopPrepQueueItem> CloneItems(IEnumerable<WorkshopPrepQueueItem> items)
    {
        return items
            .Select(x => new WorkshopPrepQueueItem
            {
                WorkshopItemId = x.WorkshopItemId,
                Quantity = x.Quantity,
            })
            .ToList();
    }

    private static WorkshopFrozenQueue? FindFrozenQueue(Configuration config, Guid queueId)
    {
        return config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == queueId);
    }

    private static bool HasDuplicateName(Configuration config, string name, Guid? exceptId)
    {
        return config.FrozenWorkshopQueues.Any(x =>
            x.Id != exceptId &&
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ItemsEqual(IReadOnlyList<WorkshopPrepQueueItem> left, IReadOnlyList<WorkshopPrepQueueItem> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].WorkshopItemId != right[index].WorkshopItemId ||
                left[index].Quantity != right[index].Quantity)
                return false;
        }

        return true;
    }
}
