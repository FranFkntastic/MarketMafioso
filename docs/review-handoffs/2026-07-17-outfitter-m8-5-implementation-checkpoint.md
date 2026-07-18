# Squire Outfitter M8.5 implementation checkpoint

Status: Implemented on `squire-outfitter-m8`; awaiting integration and live bridge verification. This checkpoint consumes the M8 contract and owns Route divergence after the start handshake.

Policy record: [[2026-07-17-outfitter-m8-m8-5-resolved-policy|Resolved M8/M8.5 policy]] · Prior checkpoint: [[2026-07-17-outfitter-m8-implementation-checkpoint|M8 implementation]]

## Implemented Route lifecycle

1. Route consumes one finalized contract, enters non-spending `Preparing`, verifies document ID/revision/canonical hash, target character/world, recovery-policy version, exact remote line bindings, route scope, remaining quantities, and layered caps, then becomes `Active`.
2. The existing rendered market-board automation still rereads the visible UI immediately before every purchase. M8.5 adds a second authority check over those rows: same item, exact NQ/HQ, fresh and complete visible coverage, remaining quantity, unit ceiling, line ceiling, and Squire plan ceiling.
3. A confirmed purchase is persisted immediately as sunk state. Every later check and plan uses remaining need, remaining line gil, and remaining global gil; verified purchases are never forgotten or made free by replanning.
4. Changed or missing planned rows may be replaced automatically by different visible listing identities only when exact item/quality and all caps remain intact. Partial exact-quality rows can be combined across worlds by the existing multi-world planner.
5. When planned rows are exhausted, Route refreshes market evidence and optimizes the complete remaining Squire route. A repeated identical recovery plan with no purchase progress pauses rather than looping.
6. If no viable route remains, Route pauses with a retry action. Workbench changes while paused invalidate retry authority and require a new Advisor/Workbench contract.
7. After an interrupted active run, persisted state is reconciled and auto-resume prepares a fresh remaining route. Manual pause/stop remains paused and does not confiscate control.

## Fixed authority boundaries

- Recovery never changes gear item, NQ/HQ identity, required quantity, target character, world scope, or any fixed cap.
- Incomplete visible listing coverage pauses; hidden rows are never treated as absent.
- Initial and recovered plans are rejected before travel if they over-procure or exceed remaining line/global caps.
- Manual Workbench lines remain separate. Squire's plan cap counts only Squire lines; manual lines retain their existing individual authority.
- All purchase evidence continues to come through visible market-board UI automation. No game structs or direct game-state reads were added.

## Verification

- Focused M8.5/route authority and route-engine suite: 54/54 passed.
- Full solution: 1,224/1,224 passed — 1,029 plugin, 193 server, 2 integration.
- `git diff --check`: clean apart from repository line-ending notices.
- Dedicated tests cover contract consumption, exact-quality row authorization, over-quantity and cap rejection, incomplete-coverage abstention, persisted sunk state, remaining-claim derivation, out-of-scope plans, changed Workbench rejection, and no-progress loop prevention.

## Required integration exit evidence

No profile was deployed from this branch. After coordinator integration, bridge verification should prove the Advisor handoff, Workbench cap editing and finalization, Route's Squire `Preparing`/`Active` status, and safe pause/retry presentation. A controlled live purchase/recovery exercise is still required before calling the market-divergence path production-proven.
