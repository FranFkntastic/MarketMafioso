# UI Automation Rules

MarketMafioso should treat game UI automation as a small state machine, not as a sequence of hopeful clicks. These rules are the project-level defaults for automation work.

## Core Rules

- Confirm context before acting. A click that starts a transition is not the same thing as owning the destination state.
- Treat IPC callbacks as scheduling signals, not semantic proof that the game UI is ready.
- Treat addon helper calls the same way. A function returning from `RunSearch`, `FireCallback`, `SelectItem`, or an IPC method only proves the request was sent; the next visible/game-state fact is the success condition.
- Wait for actionable UI state, not just addon existence. A visible addon only matters when the exact command, row, or button we intend to use is present and enabled.
- Every action needs a visible precondition and a visible postcondition.
- Model the human workflow without skipping intermediate UI states. If a user must type an item name, select an item result, and then wait for listings, automation needs separate states for all three.
- Do not disable UI modes such as fuzzy/partial search just because they look like third-party interference. First prove whether vanilla uses that mode to populate the next actionable state.
- Name states after the postcondition they actually prove. `SearchSent` must not mean `ListingsReady`, and `Interacted` must not mean the target addon opened.
- Diagnostics should record before and after values at each boundary. Do not log a pre-action value and later reason from it as though it were the final state.
- Prefer named states over sleeps. Add a delay only when the game state itself requires one and the surrounding state checks still prove where we are.
- Diagnostics should describe current state: visible tracked addons, actionable entries, and any relevant game context.
- First-item and cold-start transitions deserve special suspicion. They often include extra server, object, or addon setup time.
- When piggybacking another plugin, borrow its invariants. If that plugin waits for a session, object, or lock before acting, MarketMafioso should usually wait for the same thing.

## Retainer-Specific Rules

- Do not perform retainer UI actions until the selected retainer context is confirmed or the intended command menu is already actionable.
- Select `Entrust or withdraw items` only when the retainer command `SelectString` is visible and that localized entry exists.
- Close retainer inventory only when `InventoryRetainerLarge` or `InventoryRetainer` is visible.
- Finish AutoRetainer postprocess only after MarketMafioso has returned the UI to a stable state for AutoRetainer.
- If a manual full refresh fails, stop participating in that batch and release AutoRetainer's postprocess lock. The next manual start can opt back in.

## Failure Pattern Notes

- `no tracked addons present` during a command-menu wait usually means the automation is between UI states, not on the wrong menu.
- A first-retainer-only failure usually points to the transition from retainer list to active retainer session.
- Repeated wrong-menu failures mean the state machine is missing a branch. Repeated no-state failures mean the upstream handoff point is too early or the game transition is blocked.
- If disabling an adjacent plugin does not change the failure, stop treating the adjacent plugin as the root cause and re-check MarketMafioso's own UI-state contract.
- If an automation loops waiting for an addon that never appears, inspect the immediately preceding state. The missing action is usually the transition between the current visible addon and the desired addon.
