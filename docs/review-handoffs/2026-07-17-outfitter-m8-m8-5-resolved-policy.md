# Squire Outfitter M8 and M8.5 resolved policy

Status: Approved implementation authority. This record resolves Fran's live worksheet using explicit-choice-first and recommendation-by-default.

Source: [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#21. Decision worksheet for Fran|Decision worksheet]]

## Roadmap seam

- **M8 — pre-Route:** Advisor selection transfer, Workbench persistence and revisions, lineage, approval envelopes, visible confirmation, and creation of a finalized versioned execution contract.
- **M8.5 — Route-owned:** Begins when Route consumes that exact contract. It owns `Preparing`, preflight, immediate UI revalidation, changed-market recovery, purchases and sunk state, pause/Advisor returns, restart, and resume.
- **Invariant:** M8 grants only the authority visibly confirmed in the contract. M8.5 may automate aggressively inside it, but may not change item identity, NQ/HQ identity, or required quantity.

Governing material: [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#2. The ownership seam|Ownership seam]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8 exit gate|M8 exit]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8.5 exit gate|M8.5 exit]]

## M8 resolved policy

| Concern | Resolved behavior | Resolution |
|---|---|---|
| Contract | Persist the complete selected solution, exact-quality lines, market subset, evidence lineage, utility context, and full allocation provenance | Explicit |
| Binding | Bind document ID, revision, and canonical intent hash | Explicit |
| Envelope | Require both per-line absolute caps and a Squire plan cap | Explicit |
| Cap entry | Fran enters percentage headroom; confirmation shows derived fixed gil maxima and the contract persists only those fixed maxima | Explicit entry; recommended persistence |
| Mixed Workbench | Keep separate Squire and manual envelopes inside the single existing Workbench | Explicit |
| Lineage | Gear-semantic edits invalidate current solution integrity; procurement edits preserve gear lineage but invalidate confirmation; retain origin and structural diff | Recommended default |
| Confirmation | Require an explicit final confirmation with every authority-bearing field visible; opening Route is not confirmation | Recommended default |
| Recovery declaration | Persist `CrossWorldExactQuality/v1`, whose approved meaning is the M8.5 policy below | Recommended default after M8.5 approval |
| Policy evolution | Broader semantics require a new PolicyId/version and a newly confirmed contract | Recommended default |
| Legacy staging | Ambiguous `Either`-quality staging remains blocked | Fixed invariant |

Governing material: [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8-A. Persisted contract model|Contract]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8-B. Approval envelope|Envelope]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8-C. Revision binding and confirmation|Binding and confirmation]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8-D. Lineage preservation and invalidation|Lineage]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#M8-E. Versioned recovery-policy declaration|Policy version]]

## M8.5 resolved policy

| Concern | Resolved behavior | Resolution |
|---|---|---|
| Route start | Contract-consumption handshake pins one exact contract, then enters non-spending `Preparing` | Recommended default |
| Preflight | Reconcile contract, current evidence, remaining need, and sunk state; failure pauses before travel or purchase | Recommended default |
| UI authority | Revalidate every exact row immediately before purchase and verify every result through visible game UI | Recommended default |
| Recovery | Automatically replan across allowed worlds for the same item and exact NQ/HQ quality under `CrossWorldExactQuality/v1` | Recommended default plus bundle interpretation |
| Partial rows | Combine exact-quality rows across allowed worlds when needed | Recommended default |
| Optimization | Optimize the complete remaining route, not greedily one row at a time | Recommended default |
| Identity boundary | Different item, different NQ/HQ, or different required quantity returns through Advisor/Workbench and requires a new contract | Recommended default; reaffirmed by Fran |
| Sunk state | Verified purchases become owned inputs and remain charged against the confirmed cap; every replanning pass uses only remaining need and remaining authority | Recommended default |
| Exhausted authority | Pause with a changed-plan diff; permit waiting/refreshing or return to Advisor, but never broaden authority silently | Recommended default |
| Freshness | Authority is revision-bound with immediate UI checks, not a short wall-clock lease; irrelevant evidence generations do not invalidate it | Recommended default |
| Restart | Automatically resume only after persisted-state reconciliation, `Preparing`, preflight, and per-row UI revalidation | Explicit override |

Governing material: [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#Route-start handshake and preflight|Route start]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#10. M8.5 changed-market path|Changed market]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#11. M8.5 recovery authority|Recovery]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#13. Sunk state|Sunk state]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#14. Pause versus return to Advisor|Advisor boundary]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#15. Freshness and leases|Freshness]] · [[2026-07-17-outfitter-m8-m8-5-visual-policy-review-rev2#16. Restart and resume|Restart]]

## Explicit overrides and interpretation

- Percentage is the primary cap-entry control; it derives fixed gil caps rather than persisting a moving percentage.
- Separate Squire/manual envelopes replace the simpler Squire-only presentation while preserving a Squire-specific plan cap.
- Restart uses automatic post-reconciliation resume instead of the recommended first-release confirmation prompt.
- `Permissive/autonomous` means aggressive automation only inside the exact solution, exact quality, layered caps, allowed worlds, and Advisor boundary. It does not authorize outcome-oriented contracts, absent caps, gear reselection, quality substitution, or quantity mutation.

## Downstream boundary

M9 remains retainer targets. M10 remains portfolio allocation and UI equipping. Neither is pulled into M8.5: Route recovery cannot choose a different gear solution.
