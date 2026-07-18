# Squire Outfitter M8 implementation checkpoint

Status: Implemented on `squire-outfitter-m8`; awaiting integration/runtime verification. This checkpoint covers only the pre-Route M8 boundary. Route consumption and recovery remain M8.5.

Policy record: [[2026-07-17-outfitter-m8-m8-5-resolved-policy|Resolved M8/M8.5 policy]]

## Implemented workflow

1. A complete live Advisor frontier may hand the user-selected solution to the existing Market Acquisition Workbench. Synthetic, retained, incomplete, stale, or market-free advice cannot stage.
2. The transfer persists the selected solution, complete loadout allocation lineage, exact NQ/HQ market lots, evidence generation, utility profile/context, observation IDs, worlds, and observed prices.
3. Workbench remains the only composition surface. Staging replaces conflicting item rows with exact-quality target-quantity rows while preserving unrelated manual rows.
4. Fran's percentage headroom control derives fixed per-line unit ceilings, fixed per-line total ceilings, and a separate fixed Squire plan ceiling. Only fixed maxima enter the execution contract.
5. Gear-semantic edits invalidate current solution integrity while retaining historical Advisor provenance. Procurement/scope edits preserve lineage but invalidate prior confirmation.
6. Finalize performs explicit confirmation and persists an immutable versioned contract bound to local document ID, revision, target character/world, canonical full-intent hash, exact transfer, fixed caps, route scope, and `CrossWorldExactQuality/v1`.
7. Server synchronization continues to hash and exchange only the ordinary buy-list contract. Local Squire authority survives matching refreshes, but changed exact rows invalidate it instead of silently adopting broader authority.

## Safety invariants

- Legacy `Either`-quality Outfitter staging remains blocked.
- Route has no new recovery authority in this checkpoint.
- A hidden or mismatched line cap cannot pass finalization as the visible Squire envelope.
- Reconfirming an unchanged revision is idempotent; it does not manufacture a new contract identity.
- No game structs or direct game-state reads were introduced. Advisor evidence remains rendered-UI plus market-source evidence.

## Verification

- Focused M8/Workbench suite: 30/30 passed.
- Full solution: 1,218/1,218 passed — 1,023 plugin, 193 server, 2 integration.
- `git diff --check`: clean apart from repository line-ending notices.
- Persistence round-trip, remote-change invalidation, canonical binding, exact-quality staging, cap derivation, semantic invalidation, and server-hash separation have dedicated tests.

## M8.5 handoff

M8.5 must consume one finalized contract in a Route-start handshake, enter non-spending `Preparing`, reconcile persisted sunk state and remaining need, and revalidate visible market rows immediately before purchase. Recovery may optimize aggressively across allowed worlds only for the same item, exact quality, remaining quantity, and layered caps. Any item/quality/quantity change returns to Advisor and requires a new contract.
