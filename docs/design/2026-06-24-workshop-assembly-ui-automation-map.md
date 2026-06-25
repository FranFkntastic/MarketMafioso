# Workshop Assembly UI Automation Map

This document maps the full company workshop path that MarketMafioso's native assembly runner must handle for a queued project. The goal is to make each expected UI state explicit before optimizing or expanding the runner.

## Queue Contract

MarketMafioso's Workshop Prep queue is the source of truth. For each queue entry, the runner should complete `Quantity` finished products before advancing to the next queue entry.

The runner may resume from an already-open matching workshop project. It should not require a fresh project start if the station is already on the material delivery, phase transition, construction completion, or product retrieval path for the queued project.

## Cold Start

1. If a material delivery addon is already open and its result item matches the queued project, proceed to material delivery.
2. If a workshop branch `SelectString` is open, classify its entries before opening the crafting log.
3. If `CompanyCraftRecipeNoteBook` is open, use it to select the queued project.
4. Otherwise, find a nearby `Fabrication Station` event object, interact with it, and wait for either a branch menu or the crafting log.

Expected branch menu actions:

- `Contribute materials.` opens the active project's material delivery UI.
- `Advance to the next phase of production.` advances the current project, then the runner should reacquire the station or next branch menu.
- `Complete the construction of ...` completes final assembly, then the runner should handle the cutscene and reacquire the station.
- `Collect finished product.` starts product retrieval confirmation.
- `View company crafting log.` is a fallback path to select a new queued project.

## Project Selection

When `CompanyCraftRecipeNoteBook` is visible:

1. Select the queued project's category and type from `CompanyCraftSequence` metadata.
2. Select the queued workshop item id from the visible crafting-log rows.
3. Confirm the `Craft ...?` `SelectYesno` prompt.
4. Wait for the station branch menu or material delivery UI.

If category or type metadata is missing, fail explicitly. Do not guess a category or select by visible text alone.

## Confirmation Identity

`SelectYesno` does not expose a useful semantic prompt id through the generated client struct. The runner should therefore identify confirmations by the action MarketMafioso just caused, not by exact prompt text alone.

Pending confirmation kinds:

- `ProjectStart`: set after selecting a project from `CompanyCraftRecipeNoteBook`; accepts `Craft ...` prompts.
- `MaterialContribution`: set after the request item window has been confirmed; accepts HQ handoff warnings and `Contribute ... to the company project?` prompts.
- `ProductRetrieval`: set after selecting `Collect finished product.`; accepts `Retrieve ... from the company workshop?` prompts.
- `PhaseAdvance` and `FinalConstruction`: reserved for future confirmation prompts if those actions expose a `SelectYesno`; current observed behavior proceeds through station reacquisition and cutscene handling.

Prompt text remains useful as a broad category guard and diagnostic. Unknown visible prompts should block and log the pending confirmation kind plus the prompt text instead of being clicked or collapsed into a generic "not actionable" message.

## Material Delivery

When the material delivery addon is open:

1. Read the live project result item and material rows.
2. If the result item does not match the active queue entry, fail explicitly.
3. Find the first unfinished material row with enough inventory for one contribution step.
4. Suppress external text-advance automation before requesting the item.
5. Fire the material row contribution callback.
6. Wait for `Request` setup, select the matching item from `ContextIconMenu`, then wait for request refresh.
7. Confirm possible `SelectYesno` prompts in this order:
   - HQ handoff warning: `You are about to hand over an HQ item. Proceed?`
   - HQ trade warning: `Do you really want to trade a high-quality item?`
   - contribution prompt: `Contribute ... to the company project?`
8. Restore external text-advance automation after the request has been confirmed or after the action path is abandoned.
9. Wait for the named post-contribution lockout, then verify material progress or a valid workshop branch menu.

If the runner sees a visible `SelectYesno` during this phase, it must classify it against the current pending confirmation kind before reporting that material contribution is not actionable.

## Phase Transitions

After each contribution, the runner should expect one of these outcomes:

- The same material delivery UI remains open with advanced material progress.
- The branch menu returns with `Contribute materials.` for more work in the current phase.
- The branch menu returns with `Advance to the next phase of production.` when a phase is complete.
- The branch menu returns with `Complete the construction of ...` when the final phase is complete.
- The branch menu returns with `Collect finished product.` when construction is complete.

Advancing a phase and completing construction are station actions, not material actions. They should be logged separately and followed by reacquiring the station state.

## Cutscenes

Final construction may trigger a workshop cutscene. The runner should:

1. Detect the cutscene condition.
2. Use the same skip path as Workshoppa's observed behavior: surface the cutscene skip prompt, choose the skip option, and wait for the world UI to return.
3. Reacquire the fabrication station or active branch menu.
4. Continue to product retrieval instead of failing because the material delivery UI closed.

## Product Retrieval

When `Collect finished product.` is selected:

1. Mark `ProductRetrieval` as the pending confirmation.
2. Wait for a `SelectYesno` retrieval prompt.
3. Accept known retrieval prompt variants:
   - `Retrieve from the company workshop?`
   - `Retrieve the ... from the company workshop?`
4. Mark one project unit complete only after accepting the retrieval prompt.
5. If the queue entry still has remaining quantity, reacquire the station and repeat from cold start/open-project handling.
6. If the queue entry is complete, advance to the next queue entry.
7. If no queue entries remain, finish the runner successfully.

## Diagnostics

Every wait/failure message should include:

- runner state
- active queue entry and completed quantity
- tracked addon readiness and visibility
- visible `SelectString` entries when present
- visible `SelectYesno` prompt text when present

Unknown visible prompts should be logged as unknown prompts, not collapsed into generic "not actionable" messages. This keeps future missing branches discoverable from a single diagnostic run.
