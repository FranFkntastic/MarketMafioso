# Market Acquisition Route Runner Design

## Goal

Replace the temporary guided-route controls with a real Market Acquisition route runner that owns route lifecycle, optional diagnostics, and framework-tick progression from a prepared plan.

## UI Contract

Once a market acquisition plan is prepared, the route surface exposes these controls:

- `Start Route`
- `Start With Diagnostics`
- `Pause` / `Resume`
- `Stop`
- `Restart`

The UI must not present `/li` command textboxes or primary copy/execute buttons as the route workflow. The current Lifestream command may appear in the route table or diagnostics because it is useful evidence, but the runner owns command execution.

## Runner Lifecycle

The route runner has explicit states:

- `Idle`
- `Running`
- `Paused`
- `Completed`
- `Stopped`
- `Failed`

`Start` constructs a route from the prepared plan and begins execution. `Pause` halts framework-tick progression without discarding state. `Resume` continues from the active stop. `Stop` ends the run and closes diagnostics. `Restart` discards the current run and starts a fresh route from the current prepared plan.

## Stop Pipeline

Each world stop progresses through this read-only pipeline:

1. Send the planned Lifestream command.
2. Wait through unavailable current-world states during loading or DC travel.
3. Detect arrival on the expected world.
4. Wait for the market board item-search addon.
5. Submit the planned item search.
6. Wait for live search results.
7. Read live listings.
8. Build the live candidate decision.
9. Record the stop and advance to the next world.

This pass does not execute purchases. It establishes the lifecycle and diagnostic surface needed before purchase automation.

## Diagnostics

Diagnostic route runs write a timestamped log under the plugin config directory. Normal route runs do not write files. Diagnostics record:

- plugin version and assembly location
- route start/stop/pause/resume/restart events
- active world and stop status transitions
- Lifestream command execution
- market board search result status
- live probe and live candidate result status
- failures with exception type and message

Search-mode internals can be added to this same diagnostic file once the item-search driver is instrumented in the follow-up pass.

## Testing

Pure tests cover runner lifecycle, pause/resume behavior, automatic first command execution, stop recording, restart behavior, and diagnostic log creation. Existing guided-route session tests remain as lower-level coverage for stop state.
