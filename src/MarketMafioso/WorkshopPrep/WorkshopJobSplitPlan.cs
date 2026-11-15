using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

/// <summary>One assembler column of a split plan.</summary>
public sealed record WorkshopJobSplitColumn(WorkshopAssembler Assembler, bool IsCurrentCharacter, int AccentSlot);

/// <summary>The items one other assembler receives when a split is committed.</summary>
public sealed record WorkshopJobSplitShare(WorkshopAssembler Assembler, IReadOnlyList<WorkshopPrepQueueItem> Items);

/// <summary>What a balanced split produces: shares for other assemblers and what the current character keeps.</summary>
public sealed record WorkshopJobSplitCommit(
    Guid SourceJobId,
    string JobName,
    IReadOnlyList<WorkshopJobSplitShare> Shares,
    IReadOnlyList<WorkshopPrepQueueItem> RetainedItems);

/// <summary>
/// Explicit per-assembler allocation of a saved job's projects. Every assembler is a peer; no edit
/// moves units between assemblers on its own. Nothing outside this object changes until a commit
/// is built and applied.
/// </summary>
public sealed class WorkshopJobSplitPlan
{
    public const int MaxCellQuantity = WorkshopSavedJobShare.MaxQuantity;
    public const int AccentSlotCount = 6;

    private readonly uint[] projectIds;
    private readonly int[] planQuantities;
    private readonly List<int>[] cells;
    private readonly List<WorkshopJobSplitColumn> columns = [];

    public WorkshopJobSplitPlan(WorkshopFrozenQueue source, WorkshopAssembler currentCharacter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentCharacter);

        SourceJobId = source.Id;
        JobName = source.Name;
        CurrentCharacter = currentCharacter;
        projectIds = source.Items.Select(item => item.WorkshopItemId).ToArray();
        planQuantities = source.Items.Select(item => Math.Max(0, item.Quantity)).ToArray();
        cells = planQuantities.Select(quantity => new List<int> { quantity }).ToArray();
        columns.Add(new WorkshopJobSplitColumn(currentCharacter, IsCurrentCharacter: true, AccentSlot: 0));
    }

    public Guid SourceJobId { get; }
    public string JobName { get; }
    public WorkshopAssembler CurrentCharacter { get; }
    public int RowCount => projectIds.Length;
    public IReadOnlyList<WorkshopJobSplitColumn> Columns => columns;

    public uint ProjectId(int row) => projectIds[row];
    public int PlanQuantity(int row) => planQuantities[row];
    public int Get(int row, int column) => cells[row][column];

    public void Set(int row, int column, int quantity) =>
        cells[row][column] = Math.Clamp(quantity, 0, MaxCellQuantity);

    public int Allotted(int row) => cells[row].Sum();

    /// <summary>Allotted minus plan: positive is over-allotted, negative is unassigned.</summary>
    public int Difference(int row) => Allotted(row) - planQuantities[row];

    public int ColumnTotal(int column) => cells.Sum(row => row[column]);
    public int PlanTotal => planQuantities.Sum();
    public int AllottedTotal => Enumerable.Range(0, RowCount).Sum(Allotted);
    public int OverTotal => Enumerable.Range(0, RowCount).Sum(row => Math.Max(0, Difference(row)));
    public int UnassignedTotal => Enumerable.Range(0, RowCount).Sum(row => Math.Max(0, -Difference(row)));
    public bool IsBalanced => Enumerable.Range(0, RowCount).All(row => Difference(row) == 0);

    public bool CanCommit =>
        IsBalanced &&
        columns.Select((column, index) => (column, index))
            .Any(entry => !entry.column.IsCurrentCharacter && ColumnTotal(entry.index) > 0);

    public int IndexOf(WorkshopAssembler assembler) =>
        columns.FindIndex(column => SameCharacter(column.Assembler, assembler));

    public bool Contains(WorkshopAssembler assembler) => IndexOf(assembler) >= 0;

    /// <summary>Adds an assembler at 0 parts. Returns its column index, or -1 when already present.</summary>
    public int Add(WorkshopAssembler assembler)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        if (Contains(assembler))
            return -1;

        var usedSlots = columns.Select(column => column.AccentSlot).ToHashSet();
        var slot = Enumerable.Range(0, AccentSlotCount).FirstOrDefault(candidate => !usedSlots.Contains(candidate), -1);
        if (slot < 0)
            slot = columns.Count % AccentSlotCount;

        columns.Add(new WorkshopJobSplitColumn(
            assembler,
            SameCharacter(assembler, CurrentCharacter),
            slot));
        foreach (var row in cells)
            row.Add(0);
        return columns.Count - 1;
    }

    public void RemoveAt(int column)
    {
        columns.RemoveAt(column);
        foreach (var row in cells)
            row.RemoveAt(column);
    }

    public void SetQuantity(IEnumerable<int> rows, int column, int quantity)
    {
        foreach (var row in rows)
            Set(row, column, quantity);
    }

    /// <summary>The target gets each row's plan quantity; every other assembler is zeroed.</summary>
    public void AssignAll(IEnumerable<int> rows, int column)
    {
        foreach (var row in rows)
        {
            for (var index = 0; index < columns.Count; index++)
                cells[row][index] = index == column ? Math.Min(planQuantities[row], MaxCellQuantity) : 0;
        }
    }

    public void Unassign(IEnumerable<int> rows, int column)
    {
        foreach (var row in rows)
            cells[row][column] = 0;
    }

    /// <summary>Deals whole units round-robin across every assembler, continuing across rows in row order.</summary>
    public void SplitEvenly(IEnumerable<int> rows)
    {
        if (columns.Count == 0)
            return;

        var cursor = 0;
        foreach (var row in rows.Distinct().Order())
        {
            var dealt = new int[columns.Count];
            for (var unit = 0; unit < planQuantities[row]; unit++)
            {
                dealt[cursor]++;
                cursor = (cursor + 1) % columns.Count;
            }

            for (var index = 0; index < columns.Count; index++)
                cells[row][index] = Math.Min(dealt[index], MaxCellQuantity);
        }
    }

    public WorkshopJobSplitCommit BuildCommit()
    {
        if (!CanCommit)
            throw new InvalidOperationException("Split is not balanced or gives no other character a part.");

        var shares = new List<WorkshopJobSplitShare>();
        IReadOnlyList<WorkshopPrepQueueItem> retained = [];
        for (var column = 0; column < columns.Count; column++)
        {
            var items = ItemsFor(column);
            if (columns[column].IsCurrentCharacter)
                retained = items;
            else if (items.Count > 0)
                shares.Add(new WorkshopJobSplitShare(columns[column].Assembler, items));
        }

        return new WorkshopJobSplitCommit(SourceJobId, JobName, shares, retained);
    }

    private List<WorkshopPrepQueueItem> ItemsFor(int column) =>
        Enumerable.Range(0, RowCount)
            .Where(row => cells[row][column] > 0)
            .Select(row => new WorkshopPrepQueueItem { WorkshopItemId = projectIds[row], Quantity = cells[row][column] })
            .ToList();

    public static bool SameCharacter(WorkshopAssembler left, WorkshopAssembler right) =>
        string.Equals(left.CharacterName.Trim(), right.CharacterName.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.World.Trim(), right.World.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses "Name@World" (for example "Mira Vell@Ultros").</summary>
    public static bool TryParseAssembler(string? text, out WorkshopAssembler assembler)
    {
        assembler = null!;
        var trimmed = text?.Trim() ?? string.Empty;
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
            return false;

        var name = string.Join(' ', trimmed[..at].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var world = trimmed[(at + 1)..].Trim();
        if (name.Length == 0 || world.Length == 0 || world.Contains(' '))
            return false;

        assembler = new WorkshopAssembler(name, world);
        return true;
    }
}

public sealed record WorkshopJobSplitApplyResult(
    string Message,
    IReadOnlyList<WorkshopFrozenQueue> Delivered,
    WorkshopFrozenQueue? Retained);

public static class WorkshopJobSplitService
{
    public const string NameSeparator = " — ";

    /// <summary>
    /// Writes one saved job per share and replaces the source with what the current character
    /// keeps. A source left empty is removed. Delivery itself is the caller's job.
    /// </summary>
    public static WorkshopJobSplitApplyResult Apply(Configuration config, WorkshopJobSplitCommit commit, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(commit);

        var source = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == commit.SourceJobId)
            ?? throw new InvalidOperationException($"Saved job {commit.JobName} no longer exists.");

        var delivered = new List<WorkshopFrozenQueue>();
        foreach (var share in commit.Shares)
        {
            var job = new WorkshopFrozenQueue
            {
                Id = Guid.NewGuid(),
                Name = UniqueName(config, $"{source.Name}{NameSeparator}{share.Assembler.CharacterName}"),
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
                Items = WorkshopQueueService.CloneItems(share.Items),
                SplitSourceJobId = source.Id,
                HandoffTargetName = share.Assembler.CharacterName,
                HandoffTargetWorld = share.Assembler.World,
            };
            config.FrozenWorkshopQueues.Add(job);
            delivered.Add(job);
        }

        if (commit.RetainedItems.Count == 0)
        {
            config.FrozenWorkshopQueues.Remove(source);
            if (config.ActiveFrozenWorkshopQueueId == source.Id)
                config.ActiveFrozenWorkshopQueueId = null;

            return new($"Handed {source.Name} off in {delivered.Count} share(s).", delivered, null);
        }

        var wasLoaded = WorkshopQueueService.GetFrozenQueueState(config, source.Id) == WorkshopFrozenQueueState.Loaded;
        source.Items = WorkshopQueueService.CloneItems(commit.RetainedItems);
        source.UpdatedAt = nowUtc;
        if (wasLoaded)
            config.WorkshopPrepQueue = WorkshopQueueService.CloneItems(source.Items);

        return new($"Split {source.Name}: kept {source.Items.Sum(item => item.Quantity)} part(s), handed off {delivered.Count} share(s).", delivered, source);
    }

    private static string UniqueName(Configuration config, string name)
    {
        var trimmed = name.Length > WorkshopSavedJobShare.MaxNameLength
            ? name[..WorkshopSavedJobShare.MaxNameLength].TrimEnd()
            : name;
        var candidate = trimmed;
        for (var suffix = 2; config.FrozenWorkshopQueues.Any(queue => string.Equals(queue.Name, candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
            candidate = $"{trimmed} ({suffix})";
        return candidate;
    }
}

public enum WorkshopSavedJobHandoffStatus
{
    None,
    Delivering,
    NotDelivered,
    Offered,
    Accepted,
    Declined,
    Withdrawn,
}

public static class WorkshopSavedJobHandoff
{
    public static bool IsHandoff(WorkshopFrozenQueue queue) => !string.IsNullOrWhiteSpace(queue.HandoffTargetName);

    public static WorkshopAssembler? Target(WorkshopFrozenQueue queue) =>
        IsHandoff(queue) ? new WorkshopAssembler(queue.HandoffTargetName!, queue.HandoffTargetWorld ?? string.Empty) : null;

    public static WorkshopSavedJobHandoffStatus Resolve(
        WorkshopFrozenQueue queue,
        IReadOnlyList<WorkshopHandoffView> outgoing,
        bool delivering)
    {
        if (!IsHandoff(queue))
            return WorkshopSavedJobHandoffStatus.None;
        if (delivering)
            return WorkshopSavedJobHandoffStatus.Delivering;
        if (queue.HandoffId is not { } handoffId)
            return WorkshopSavedJobHandoffStatus.NotDelivered;

        var view = outgoing.FirstOrDefault(handoff => handoff.HandoffId == handoffId);
        return (view?.State ?? queue.HandoffState ?? WorkshopHandoffState.Offered) switch
        {
            WorkshopHandoffState.Accepted => WorkshopSavedJobHandoffStatus.Accepted,
            WorkshopHandoffState.Declined => WorkshopSavedJobHandoffStatus.Declined,
            WorkshopHandoffState.Withdrawn => WorkshopSavedJobHandoffStatus.Withdrawn,
            _ => WorkshopSavedJobHandoffStatus.Offered,
        };
    }

    public static string Label(WorkshopSavedJobHandoffStatus status) => status switch
    {
        WorkshopSavedJobHandoffStatus.Delivering => "Offering",
        WorkshopSavedJobHandoffStatus.NotDelivered => "Not delivered",
        WorkshopSavedJobHandoffStatus.Offered => "Offered",
        WorkshopSavedJobHandoffStatus.Accepted => "Accepted",
        WorkshopSavedJobHandoffStatus.Declined => "Declined",
        WorkshopSavedJobHandoffStatus.Withdrawn => "Withdrawn",
        _ => string.Empty,
    };
}
