# Squire Outfitter M8/M8.5 Review Corrections

## Scope

This additive checkpoint resolves the three coordinator-blocking integration findings against commits `597b14f` and `c8dcee0`. It does not advance M9, deploy a profile, or broaden Route authority.

## Corrections

1. **Recoverable market divergence now enters recovery.** Both route-engine candidate-authorization sites stop the original runner, persist `RecoveryNeeded`, and trigger complete remaining-route preparation. The guided-route presenter exposes Squire recovery instead of generic Resume. Deliberate manual Pause remains `Paused` plus a paused runner and resumes the original plan without replanning.
2. **Finalized world authority is exact.** Execution-contract schema v2 persists the concrete authorized-world set derived from the visible Region, Current Data Center, or explicit Data Centers scope. Old v1 contracts are re-finalized rather than reused. Same-region cross-DC rejection is covered.
3. **Impossible restart is visibly terminal.** No-plan, preparation-exception, initial-preflight, recovery-preflight, and runner-start failures persist `Paused` with Retry/return-to-Advisor guidance. `Paused` is not eligible for the 30-second automatic-resume loop. Persisted sunk purchases survive failed restart preflight.
4. **Character scope is a Workbench revision.** Changing target character/world increments `LocalRevision`, preserves exact gear lineage, and invalidates the finalized confirmation contract.

## Lifecycle evidence

- changed/incomplete visible listing authority -> original runner stopped -> `RecoveryNeeded` -> recovery plan -> `Active`;
- manual Pause -> generic Resume -> `Active`, with no recovery transition;
- no viable recovery plan -> `Paused` once -> automatic retry disabled;
- failed initial/recovery preflight -> `Paused`, with sunk purchases retained;
- Region accepts same-region cross-DC worlds; Current Data Center and explicit Data Centers reject worlds outside their exact finalized sets.

## Verification

- Plugin: 1,038/1,038 passed.
- Server: 193/193 passed.
- Integration: 2/2 passed.
- Total: 1,233/1,233 passed.
- No push or deployment performed.

## Remaining integration exit

After coordinator review and integration, live Agent Bridge proof still needs to show a finalized schema-v2 contract, a changed listing entering recovery without generic Resume, a recovered plan becoming active, and a no-plan recovery exposing Retry/return-to-Advisor without repeating automatically.
