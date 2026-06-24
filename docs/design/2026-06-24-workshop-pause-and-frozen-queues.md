# Workshop Pause and Frozen Queues Design

## Goal

Add two workflow features to Workshop Prep:

- A real native-assembly pause/resume state that temporarily suspends automation without cancelling the current run.
- Frozen queues: named snapshots of workshop prep queues that can be loaded later as fresh active queues.

This design also changes active queue semantics during native assembly: after a finished project is retrieved, the active queue quantity for that project decrements by one and is saved immediately.

## Product Semantics

The active prep queue remains the source of truth for current work. Material summaries, retainer restock, manifest export, VIWI handoff, and native assembly all continue to operate on `Configuration.WorkshopPrepQueue`.

Frozen queues are separate saved snapshots. They are not presets, profiles, automation settings, or execution sessions. Loading a frozen queue copies its items into the active prep queue. Editing or assembling the active queue never mutates the frozen queue unless the user explicitly overwrites that frozen queue.

Native assembly runtime state remains transient. Paused execution is kept in memory only. Plugin reload, crash, or Dalamud unload returns the runner to idle; the active queue still reflects completed work because project retrieval decrements it.

## Pause, Resume, and Stop

Pause is a runtime suspend:

- Unsubscribe the runner from framework ticks.
- Preserve `activePlan`, active entry index, active completed quantity, active material id, pending progress material id, pending progress steps, diagnostics, and current progress.
- Set runner state to `Paused`.
- Leave game UI alone.

Resume is a runtime continue:

- Only allowed from `Paused`.
- Resubscribe to framework ticks.
- Continue from the preserved state.
- Reacquire live workshop UI using the same state-machine path already used for normal execution.

Stop remains cancellation:

- Unsubscribe from framework ticks.
- Set runner state to `Stopped`.
- Record/close diagnostics.
- Do not preserve runtime progress for resume.
- Leave the active prep queue as-is, including any quantities already decremented after successful retrieval.

## Active Queue Decrementing

When native assembly successfully retrieves a finished product:

1. Find the matching `WorkshopPrepQueueItem` in `Configuration.WorkshopPrepQueue`.
2. Decrement `Quantity` by one.
3. Remove the queue row when quantity reaches zero.
4. Save configuration immediately.
5. Continue the existing in-memory run using the runner plan, so the current assembly pass does not need to rebuild after every decrement.

This makes the visible active queue represent remaining work. If the runner stops after completing 5 of 16 parts, the active queue now shows 11 remaining.

The runner plan and progress counters still track the current run. Queue decrementing is a persisted side effect of successful product retrieval, not the runner's source of progress during the active run.

## Frozen Queue Model

Add persisted frozen queue records to `Configuration`:

```csharp
public List<WorkshopFrozenQueue> FrozenWorkshopQueues { get; set; } = new();
public Guid? ActiveFrozenWorkshopQueueId { get; set; }
```

```csharp
[Serializable]
public sealed class WorkshopFrozenQueue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<WorkshopPrepQueueItem> Items { get; set; } = new();
}
```

`ActiveFrozenWorkshopQueueId` means "the active queue was loaded from, or last saved to, this frozen queue." It is cleared when the active queue is manually edited in a way that makes it diverge from the frozen queue.

## Frozen Queue Operations

Freeze current queue:

- Enabled only when the active queue is non-empty and native assembly is idle.
- Creates a new `WorkshopFrozenQueue` with a user-provided name.
- Stores a deep copy of the active queue items.
- Sets `ActiveFrozenWorkshopQueueId` to the new frozen queue id.

Overwrite frozen queue:

- Enabled when the active queue is non-empty, native assembly is idle, and an active frozen queue id exists.
- Replaces that frozen queue's items with a deep copy of the active queue.
- Updates `UpdatedAt`.

Load frozen queue:

- Enabled only when native assembly is idle.
- Replaces `Configuration.WorkshopPrepQueue` with a deep copy of the selected frozen queue's items.
- Sets `ActiveFrozenWorkshopQueueId`.
- Requires confirmation if the active queue is non-empty and differs from the selected frozen queue.

New queue:

- Enabled only when native assembly is idle.
- Clears the active prep queue.
- Clears `ActiveFrozenWorkshopQueueId`.
- Requires confirmation when the active queue is non-empty.

Delete frozen queue:

- Enabled only when native assembly is idle.
- Removes the selected frozen queue.
- Clears `ActiveFrozenWorkshopQueueId` if it pointed to the deleted queue.

Duplicate frozen queue:

- Enabled only when native assembly is idle.
- Creates a new frozen queue with copied items and a distinct name.

Rename frozen queue:

- Enabled only when native assembly is idle.
- Updates name and `UpdatedAt`.

## UI Design

Add a compact queue toolbar above the active prep queue table:

- Current queue label: `Unsaved Queue`, `Frozen: <name>`, or `Modified from: <name>`.
- `New Queue`
- `Freeze Current Queue`
- `Save Frozen Queue` when `ActiveFrozenWorkshopQueueId` is set and active queue is non-empty.
- `Load Frozen Queue`
- `Manage Frozen Queues`

Keep the active queue table dense and unchanged except for quantity decrementing during native assembly.

Frozen queue management should be a small modal or popup, not a full new tab:

- Columns: name, project count, total quantity, updated date.
- Actions: Load, Rename, Duplicate, Delete.

Assembly controls:

- Idle: `Start Native Assembly`, `Start Assembly With Diagnostics`
- Running: `Pause Assembly`, `Stop Assembly`
- Paused: `Resume Assembly`, `Stop Assembly`

Queue editing, loading, freezing, clearing, renaming, duplicating, and deleting are disabled while assembly is running or paused. The user must Stop first.

## Error Handling

Frozen queue operations fail explicitly when:

- The active queue is empty and the operation requires content.
- The frozen queue id is missing.
- The new frozen queue name is blank.
- The new frozen queue name duplicates another frozen queue name case-insensitively.
- Native assembly is running or paused.

Queue decrementing should fail safely:

- If a completed project is missing from the active queue, log a warning and continue the runner. The completed product was already retrieved, so the automation should not roll back.
- If quantity is already less than one, remove the row and save.

## Tests

Pure tests should cover:

- Frozen queue creation deep-copies active queue items.
- Loading a frozen queue deep-copies items into the active queue.
- Overwriting updates items and `UpdatedAt`.
- Duplicate names are rejected.
- Active frozen queue id is cleared when active queue diverges.
- New queue clears active items and active frozen id.
- Queue decrement removes a row at zero.
- Queue decrement leaves frozen queues unchanged.
- Runner pause preserves runtime fields and blocks tick execution.
- Runner resume continues from `Paused`.
- Stop remains terminal and does not resume.

Manual in-game verification should cover:

- Pause during material delivery.
- Pause while a branch menu is visible.
- Resume after pause.
- Stop after pause.
- Queue quantity decrement after finished product retrieval.
- Loading a frozen queue, assembling part of it, stopping, and confirming the frozen queue remains unchanged.
