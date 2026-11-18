using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopQueueOperationResult(bool Success, string Message, Guid? QueueId = null, string? Name = null);

/// <summary>One saved job taken out by a delete, with what the delete cleared alongside it.</summary>
public sealed record WorkshopRemovedSavedJob(
    WorkshopFrozenQueue Job,
    int Index,
    bool WasActive,
    TradeQueueRecipientBinding? ReleasedBinding);

/// <summary>
/// Everything one delete removed, kept for a single undo. The prep queue and Trade Queue contents are
/// captured so undo only re-links the active job or binding while nothing has moved on since.
/// </summary>
public sealed record WorkshopSavedJobRemoval(
    IReadOnlyList<WorkshopRemovedSavedJob> Jobs,
    IReadOnlyList<WorkshopPrepQueueItem> PrepQueueAtRemoval,
    IReadOnlyList<TradeQueueItem> TradeQueueAtRemoval);

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
            Origin = source.Origin,
        };

        config.FrozenWorkshopQueues.Add(frozen);
        return new(true, $"Duplicated frozen workshop queue {source.Name}.", frozen.Id);
    }

    /// <summary>
    /// Adds a shared saved job as a new entry. It never replaces an existing saved job or touches
    /// the active queue; a name collision gets a numbered suffix instead.
    /// </summary>
    public static WorkshopQueueOperationResult ImportFrozenQueue(
        Configuration config,
        string name,
        IEnumerable<WorkshopPrepQueueItem> items,
        DateTime nowUtc,
        WorkshopSavedJobOrigin origin = WorkshopSavedJobOrigin.Manual)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return new(false, "Frozen queue name is required.");

        var clonedItems = CloneItems(items);
        if (clonedItems.Count == 0)
            return new(false, "Saved job contains no projects.");

        var uniqueName = normalizedName;
        for (var suffix = 2; HasDuplicateName(config, uniqueName, exceptId: null); suffix++)
            uniqueName = $"{normalizedName} ({suffix})";

        var frozen = new WorkshopFrozenQueue
        {
            Id = Guid.NewGuid(),
            Name = uniqueName,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            Items = clonedItems,
            Origin = origin,
        };

        config.FrozenWorkshopQueues.Add(frozen);
        return new(true, $"Imported saved job {uniqueName}.", frozen.Id, uniqueName);
    }

    /// <summary>
    /// Validates shared saved-job text and stores it as a new saved job. Shared by the clipboard,
    /// file, and IPC imports so all of them apply the same validation, limits, and collision naming.
    /// </summary>
    public static WorkshopQueueOperationResult ImportSavedJob(
        Configuration config,
        string? text,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        DateTime nowUtc,
        WorkshopSavedJobOrigin origin = WorkshopSavedJobOrigin.Manual)
    {
        var parsed = WorkshopSavedJobShare.Parse(text, projects);
        return parsed.Success
            ? ImportFrozenQueue(config, parsed.Name, parsed.Items, nowUtc, origin)
            : new(false, parsed.Message);
    }

    public static WorkshopQueueOperationResult DeleteFrozenQueue(Configuration config, Guid queueId)
    {
        var removal = DeleteFrozenQueues(config, [queueId]);
        return removal == null
            ? new(false, "Frozen workshop queue was not found.")
            : new(true, $"Deleted frozen workshop queue {removal.Jobs[0].Job.Name}.");
    }

    /// <summary>
    /// Removes saved jobs and returns what <see cref="RestoreFrozenQueues"/> needs to put them back,
    /// or null when none of them exist.
    /// </summary>
    public static WorkshopSavedJobRemoval? DeleteFrozenQueues(Configuration config, IReadOnlyCollection<Guid> queueIds)
    {
        var prepQueue = CloneItems(config.WorkshopPrepQueue);
        var tradeQueue = CloneTradeItems(config.TradeQueueItems);
        var removed = new List<WorkshopRemovedSavedJob>();
        foreach (var queueId in queueIds.Distinct())
        {
            var index = config.FrozenWorkshopQueues.FindIndex(x => x.Id == queueId);
            if (index < 0)
                continue;

            var frozen = config.FrozenWorkshopQueues[index];
            config.FrozenWorkshopQueues.RemoveAt(index);
            var wasActive = config.ActiveFrozenWorkshopQueueId == queueId;
            if (wasActive)
                config.ActiveFrozenWorkshopQueueId = null;
            var binding = config.TradeQueueBinding?.ShareJobId == queueId ? config.TradeQueueBinding : null;
            ReleaseTradeQueueBinding(config, queueId);
            removed.Add(new WorkshopRemovedSavedJob(frozen, index, wasActive, binding));
        }

        return removed.Count == 0 ? null : new WorkshopSavedJobRemoval(removed, prepQueue, tradeQueue);
    }

    /// <summary>
    /// Puts deleted saved jobs back where they were. The active-job link and Trade Queue binding come back
    /// only when nothing has replaced them and the prep queue or Trade Queue is unchanged since the delete.
    /// </summary>
    public static WorkshopQueueOperationResult RestoreFrozenQueues(Configuration config, WorkshopSavedJobRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        var restored = new List<WorkshopFrozenQueue>();
        // Reinsert in reverse removal order so each stored index refers to the list it was taken from.
        foreach (var entry in removal.Jobs.Reverse())
        {
            if (FindFrozenQueue(config, entry.Job.Id) != null)
                continue;
            config.FrozenWorkshopQueues.Insert(Math.Clamp(entry.Index, 0, config.FrozenWorkshopQueues.Count), entry.Job);
            restored.Add(entry.Job);
        }

        if (restored.Count == 0)
            return new(false, "Nothing to restore.");

        var active = removal.Jobs.FirstOrDefault(entry => entry.WasActive && restored.Contains(entry.Job));
        if (active != null && config.ActiveFrozenWorkshopQueueId == null &&
            ItemsEqual(config.WorkshopPrepQueue, removal.PrepQueueAtRemoval))
            config.ActiveFrozenWorkshopQueueId = active.Job.Id;

        var bound = removal.Jobs.FirstOrDefault(entry => entry.ReleasedBinding != null && restored.Contains(entry.Job));
        if (bound != null && config.TradeQueueBinding == null &&
            TradeItemsEqual(config.TradeQueueItems, removal.TradeQueueAtRemoval))
            config.TradeQueueBinding = bound.ReleasedBinding;

        var first = restored[^1];
        return restored.Count == 1
            ? new(true, $"Restored saved job {first.Name}.", first.Id, first.Name)
            : new(true, $"Restored {restored.Count} saved jobs.", first.Id, first.Name);
    }

    /// <summary>Clears the Trade Queue recipient binding when it belongs to a share that is being deleted.</summary>
    public static void ReleaseTradeQueueBinding(Configuration config, Guid deletedShareId)
    {
        if (config.TradeQueueBinding?.ShareJobId == deletedShareId)
            config.TradeQueueBinding = null;
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

    private static List<TradeQueueItem> CloneTradeItems(IEnumerable<TradeQueueItem> items) =>
        items.Select(item => new TradeQueueItem { ItemId = item.ItemId, ItemName = item.ItemName, Quantity = item.Quantity }).ToList();

    private static bool TradeItemsEqual(IReadOnlyList<TradeQueueItem> left, IReadOnlyList<TradeQueueItem> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First.ItemId == pair.Second.ItemId && pair.First.Quantity == pair.Second.Quantity);

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
