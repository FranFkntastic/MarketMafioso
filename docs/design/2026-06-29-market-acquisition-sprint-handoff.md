# Market Acquisition Sprint Handoff - 2026-06-29

## Current Deployed State

- Branches: `main` and `local-dev` are aligned at `4036d54`.
- Dev plugin deployed from `main@4036d54`.
- Visible manifest version: `1.1.216.32499`.
- Verified target DLL: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\_deployed\MarketMafioso\MarketMafioso.dll`.
- Target DLL SHA256: `7EF326F7ED206DFAF5F53D2265B8ABC2F8722AF015F4836FA6DABBD4AB0A1BF8`.
- Dev receiver/dashboard was last smoke-tested after deploying `local-dev@c055e4a`; later commits are plugin diagnostics/docs only unless noted otherwise.

## Verification

Latest focused verification:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPagination|FullyQualifiedName~MarketBoardListingReaderTests|FullyQualifiedName~MarketBoardAccumulatedReadResultTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests" -v minimal
```

Result: build passed with 0 warnings/errors; focused tests passed `33/33`.

## Implemented This Sprint

- Market-board listing reads now surface fixed-cache truncation instead of flattening it into ordinary no-stock behavior.
- Input capture logs include `InfoProxyPageInterface` request ids and listing preview fields needed to investigate deeper pagination.
- Diagnostics window shows pagination status from the current read result, including request id transitions.
- Diagnostics window has a guarded `Probe Next Listing Page` control, intentionally disabled until live captures prove the safe page-request contract.
- `MarketBoardPaginationState` and `MarketBoardPaginationProbe` provide pure, tested pagination classification.
- `MarketBoardAccumulatedReadResult` safely merges unique visible listings from multiple reads for the same item/world, rejects mixed item/world reads, and can feed live candidate planning.
- Candidate planning can select safe rows from accumulated later-page reads once a safe page-loading mechanism exists.
- The plugin UI and roadmap both now document the exact before/after capture sequence for deeper-pagination testing.

## Remaining Gates

### Live-only: Natural Pagination Evidence

The only remaining deeper-pagination gate is proving how the game moves beyond the visible `InfoProxyItemSearch.Listings` cache.

Required capture:

1. Search an item with more than 100 market-board listings.
2. Open Market Acquisition Diagnostics and confirm the read is truncated.
3. Click `Capture Input State` before touching market-board result controls.
4. Use a normal human UI action to reveal deeper results, if the game exposes one.
5. Click `Capture Input State` again immediately after that action.
6. Click `Finish Capture Log`.
7. Compare `infoProxyCurrentRequestId`, `infoProxyNextRequestId`, and `infoProxyListingPreview` between the two capture entries.

Passing evidence for automation:

- same item id,
- same world,
- request ids advance meaningfully,
- listing preview changes to deeper rows.

If that evidence is not present, deeper pagination stays deferred and route execution should continue to report `VisibleCacheExhausted` / `SkippedVisibleCacheExhausted` rather than guessing at hidden rows.

### Live-only: Multi-item Opportunistic Validation

The route loop has source support for same-world multi-item advancement and opportunistic subtasks, but still needs live validation that:

- multiple item lines can be probed and purchased or skipped on one world stop,
- stale candidate/read state does not leak from item A into item B,
- visible-cache exhaustion is preserved as a skip reason during live routes,
- post-run diagnostics remain clear after mixed complete/skipped line outcomes.

## Next Implementation Plan After Capture

If live capture proves a safe page transition:

1. Add a narrow pagination service that performs only the proven request action.
2. Before every page request, snapshot item id, world, current request id, next request id, and first visible listing ids.
3. After the request, require same item/world and a coherent request/listing transition.
4. Merge the new read into `MarketBoardAccumulatedReadResult`.
5. Rebuild the live candidate plan from accumulated reads.
6. If no safe row is found and the accumulated read is still truncated, keep `VisibleCacheExhausted`.
7. Add focused tests for continuation acceptance, wrong-item rejection, wrong-world rejection, unchanged-page refusal, and later-page candidate selection.
8. Live-test on a low-value item with more than 100 reported listings before enabling it in normal route execution.

If live capture does not prove a safe transition:

1. Keep the guarded probe control disabled.
2. Keep deeper pagination in Deferred.
3. Preserve explicit visible-cache exhaustion diagnostics.
4. Consider a later packet-level investigation only if the value outweighs the risk.

