# Native Workshop Runner Design

## Goal

Add an optional MarketMafioso-native workshop assembly runner for projects in the MarketMafioso Workshop Prep queue. The runner should execute the queue that MarketMafioso already owns, while preserving VIWI Workshoppa handoff as an alternate path.

This is an intentional product-boundary update. Earlier Workshop Prep scope stopped at project selection, material math, retainer restock, and VIWI queue handoff. That boundary still protects against cloning Workshoppa wholesale, but it is too tight for improving assembly-loop timing, diagnostics, and recovery behavior. MarketMafioso may own execution for its own prep queue; it must not mirror Workshoppa's full module surface.

## Scope

In scope:

- Execute the persisted `Configuration.WorkshopPrepQueue` as the source of truth.
- Start, stop, and report status from `/mmf` Workshop Prep.
- Use state-machine UI automation for company workshop project assembly.
- Keep VIWI queue sync available for users who prefer Workshoppa.
- Use conservative contribution timing at first, including a guarded post-contribution delay for the known material contribution lockout.
- Provide detailed diagnostics when the game UI is not in the expected state.

Out of scope:

- Leveling mode, random target selection, Grindstone logic, or Workshoppa profile migration.
- Persisting transient runner progress across plugin reloads.
- Replacing VIWI IPC.
- Running projects that are not in the MarketMafioso prep queue.
- Bypassing material requirements, server-side lockouts, or normal workshop UI constraints.

## Product Boundary

Workshop Prep owns:

- Project browser, favorites, multiselect, and queue quantities.
- Material requirement aggregation.
- Player and retainer availability summaries.
- Retainer material withdrawal.
- Optional VIWI handoff.
- Optional native assembly for the same prep queue.

Workshop Prep does not own:

- VIWI or Workshoppa configuration.
- A second complete Workshoppa feature set.
- Workshoppa queue state as source of truth.
- Long-running execution state that survives reloads.

The user-facing model should be simple: "prepare the queue here, restock here, then either send it to VIWI or run it here."

## UX

The Workshop Prep tab should keep its current workflow order:

1. Prep Queue
2. Materials
3. Actions

Actions should gain native-runner controls without burying the existing buttons:

- `Refresh Retainer Cache`
- `Restock Materials From Retainers`
- `Start Native Assembly`
- `Stop Assembly` while the runner is active
- `Send Queue To VIWI`
- `Clear Prep Queue`

The native runner status should be visible in the existing status area. It should report:

- current runner state
- active project name and quantity progress when known
- last action taken
- why it is waiting when blocked
- failure details with visible addon state

The `Send Queue To VIWI` button should remain an explicit confirmation flow because it mutates another plugin's queue. Native assembly should not require a confirmation unless it would start with missing materials; missing materials should block with a clear status instead of attempting execution.

## Architecture

Add a small native assembly subsystem under `MarketMafioso/WorkshopPrep/`.

Recommended units:

- `WorkshopAssemblyModels.cs`
  - runner state enum
  - immutable queue snapshot records
  - progress and status records
  - UI action result records
- `WorkshopAssemblyPlanBuilder.cs`
  - converts `WorkshopPrepQueueItem` plus catalog definitions into an ordered execution snapshot
  - rejects unknown projects and non-positive quantities
  - computes material requirements needed for preflight checks
- `WorkshopAssemblyPreflightService.cs`
  - checks that the queue is non-empty
  - checks player inventory against material requirements
  - returns explicit blocking messages instead of fallback behavior
- `WorkshopAssemblyTiming.cs`
  - centralizes timing constants and naming
  - starts with conservative contribution lockout timing
  - makes non-lockout waits frame/state-driven rather than blanket sleeps
- `WorkshopAssemblyUiAutomation.cs`
  - contains unsafe addon and agent interactions
  - exposes named operations such as "is fabrication station ready", "select project", "submit material", and "confirm material delivery"
  - describes visible UI state for diagnostics
- `WorkshopAssemblyRunner.cs`
  - owns the state machine
  - subscribes to framework ticks while running
  - updates status and progress
  - stops cleanly on completion, user stop, failure, or plugin disposal

The existing `MainWindow` should only call runner methods and render runner state. It should not contain assembly automation logic.

## State Machine

Initial states should be intentionally small:

- `Idle`
- `Preflight`
- `WaitingForFabricationStation`
- `OpeningProject`
- `WaitingForMaterialRequest`
- `SubmittingMaterial`
- `WaitingForContributionLockout`
- `ConfirmingContribution`
- `AdvancingProject`
- `Complete`
- `Stopped`
- `Failed`

The runner should only fire an action when the expected visible precondition is true. Every action should have a visible postcondition, a timeout, and a diagnostic message.

The runner should use `IFramework.Update` or `RunOnTick` consistently. It should avoid long async loops for frame-by-frame UI work; the retainer restock service already uses async loops because it is a bounded retainer traversal, but the assembly runner is closer to an active game UI state machine.

## Timing

Do not introduce a universal `100ms` delay. The point of native execution is better state awareness, not just faster clicking.

Timing rules:

- Use frame-driven checks for addon appearance, menu readiness, project selection readiness, and confirmation windows.
- Keep a named post-contribution lockout delay initially. Source investigation of VIWI/Workshoppa suggests the normal material contribution path uses a `1s` delay, and nearby comments mention hard lower bounds for related workshop transitions.
- The first implementation should set `PostContributionLockout = 1s`.
- Later optimization can lower that value only after logging proves the UI accepts the next contribution reliably.
- Any future "fast mode" should be explicit and diagnostic-heavy.

The timing class should make the reason for each delay readable. A line like `PostContributionLockout` is acceptable; an unexplained `AddSeconds(1)` is not.

## Error Handling

Failures should be explicit and actionable:

- empty queue
- unknown project id
- missing player materials
- not near or not targeting a fabrication station
- expected addon not visible or not ready
- expected project or material entry not found
- confirmation window missing
- contribution did not update observed progress

The runner should set state to `Failed`, stop itself, and leave the prep queue intact. User stop should set state to `Stopped`, not `Failed`.

Diagnostics should include tracked addon visibility/readiness and the active project/material identifiers where available.

## Persistence

Persist:

- prep queue
- favorites
- user configuration that intentionally affects future sessions

Do not persist:

- current runner state
- current material index
- current project index
- last transient addon state
- automation progress

If the plugin reloads mid-run, it should come back idle with the prep queue unchanged.

## Tests

Automated tests should cover the pure parts:

- plan building from queue and catalog
- rejection of unknown projects and invalid quantities
- material aggregation for preflight
- status classification and completion summaries
- runner transition policy where it can be tested without game pointers

Manual in-game verification is required for UI automation:

- start blocked when not at a fabrication station
- start blocked when materials are missing
- successful one-project assembly
- successful multi-project queue assembly
- user stop during wait state
- failure diagnostics when an expected addon is closed manually
- VIWI handoff still works after the native runner is added

## Source And Attribution Notes

VIWI Workshoppa is a behavioral reference for workshop assembly flow and timing concerns. MarketMafioso should not copy VIWI source wholesale for this pass. If any concrete code is imported later, preserve the BSD 3-Clause license notice and update README attribution.

Even without copied code, README should continue to acknowledge VIWI/Workshoppa as a direct reference and integration target.
