# Market Board Deeper Pagination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend Market Acquisition beyond the readable 100-listing `InfoProxyItemSearch` cache without guessing at unsafe game internals.

**Architecture:** Keep the current visible-cache truncation behavior as the safe fallback. First capture and prove the natural game pagination/request transition, then add a narrow pagination service that can request and merge additional market-board pages only when the active search item, world, and request ids remain coherent.

**Tech Stack:** C# 12, Dalamud/FFXIVClientStructs, xUnit, existing Market Acquisition route diagnostics.

---

## Current Evidence

- `InfoProxyItemSearch.Listings` is a fixed `FixedSizeArray100<MarketBoardListing>`.
- `InfoProxyItemSearch.ListingCount` can report more rows than `Listings.Length`.
- `InfoProxyPageInterface` exposes `CurrentRequestId` and `NextRequestId`.
- `InfoProxyInterface.RequestData()` is virtual function 3 and is documented as generating an info-proxy-specific network request.
- `InfoProxyPageInterface.AddPage(nint packetPtr)` is documented as handling received page packets and may dispatch pagination/fetch work internally, but it requires a packet pointer and is not a safe "next page" command by itself.
- MarketMafioso already records truncation and preserves `SkippedVisibleCacheExhausted` rather than pretending no safe stock exists.
- `MarketBoardInputCaptureReader` records `infoProxyEntryCount`, `infoProxyCurrentRequestId`, and `infoProxyNextRequestId` for future captures.
- `MarketBoardPaginationState` and `MarketBoardPaginationProbe` now provide pure, tested diagnostics for "not truncated", "request ids incoherent", "ready for live probe", "advanced", "wrong continuation", and "unchanged".
- `MarketBoardReadResult` carries `InfoProxyPageInterface` request ids into the diagnostics window, so truncation logs can be interpreted without relying only on separate input-capture files.

## Non-Negotiable Safety Rules

- Do not call `AddPage(...)` with invented or stale packet pointers.
- Do not call `RequestData()` until captures prove which fields and request ids must be set before the call.
- Do not merge listings from different item ids or worlds.
- Do not purchase from unobserved pages. A listing is safe only after it is visible in the live market-board cache and passes the normal live candidate checks.
- If pagination cannot be proven in a live capture, keep `VisibleCacheExhausted` as the terminal line status.

## Task 1: Capture Natural Pagination State Transitions

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardInputCaptureReader.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardInputCaptureReaderTests.cs` if a practical fake can be introduced without large unsafe scaffolding
- Docs: `docs/design/2026-06-28-market-acquisition-next-feature-list.md`

**Live Capture Playbook:**

1. Search an item with more than 100 market-board listings.
2. Open Market Acquisition Diagnostics and confirm it says the read is truncated.
3. Click `Capture Input State` before touching the market-board result controls.
4. Use only a normal human UI action to reveal deeper results, if the game exposes one.
5. Click `Capture Input State` again immediately after the attempted page/scroll/load action.
6. Click `Finish Capture Log`.
7. Compare `infoProxyCurrentRequestId`, `infoProxyNextRequestId`, and `infoProxyListingPreview` between the two capture entries.

Passing evidence for automation is a same-item, same-world capture pair where request ids advance and the listing preview changes. If ids and preview do not change, deeper pagination remains deferred and the route should continue to use `VisibleCacheExhausted`.

- [x] **Step 1: Build and deploy the current request-id capture fields**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

Expected: build succeeds, deploy script reports matching source/target hashes, and the visible manifest version changes.

Status: Complete. The dev plugin was deployed from `main@7339ce2` with visible manifest `1.1.213.30767`; read results and input captures both carry pagination request IDs, and accumulated-read planner support is available for future live pagination validation.

- [ ] **Step 2: Capture before natural page movement**

In game, search an item with more than 100 listings. Before touching any market-board page controls or scroll controls, run the input capture and save the generated `input-capture-*.log`.

Expected log evidence:

```text
infoProxySearchItemId=<target item id>
infoProxyReportedListingCount=<number greater than 100>
infoProxyListingCapacity=100
infoProxyListingCountTruncated=True
infoProxyCurrentRequestId=<value A>
infoProxyNextRequestId=<value B>
```

- [ ] **Step 3: Capture after natural page movement or load-more behavior**

Use the normal in-game interaction that reveals more listings, if such an interaction exists for the current market-board UI. Immediately run another input capture.

Expected evidence if the game supports a next-page transition:

```text
infoProxySearchItemId=<same target item id>
infoProxyCurrentRequestId=<value different from or meaningfully advanced beyond the before capture>
infoProxyNextRequestId=<value different from or meaningfully advanced beyond the before capture>
infoProxyListingPreview=<different listing ids than the before capture>
```

Expected evidence if the game does not expose deeper rows through the current UI:

```text
infoProxySearchItemId=<same target item id>
infoProxyCurrentRequestId=<unchanged>
infoProxyNextRequestId=<unchanged>
infoProxyListingPreview=<same first visible listing ids>
```

- [ ] **Step 4: Record the result**

Append the capture finding to `docs/design/2026-06-28-market-acquisition-next-feature-list.md` under "Current Implementation Notes".

If natural page movement is not observable, stop this plan and keep deeper pagination deferred.

## Task 2: Add A Pure Pagination State Model

Status: Complete in `MarketMafioso/MarketAcquisition/MarketBoardPaginationState.cs` with tests in `MarketMafioso.Tests/MarketAcquisition/MarketBoardPaginationStateTests.cs`.

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPaginationState.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPaginationStateTests.cs`

- [x] **Step 1: Write tests for safe/unsafe pagination states**

Create tests covering:

```csharp
[Fact]
public void CanRequestNextPage_ReturnsFalseWhenNotTruncated()
{
    var state = new MarketBoardPaginationState(
        itemId: 5064,
        worldName: "Siren",
        reportedListingCount: 42,
        readableListingCount: 42,
        listingCapacity: 100,
        currentRequestId: 1,
        nextRequestId: 2);

    Assert.False(state.CanRequestNextPage);
}

[Fact]
public void CanRequestNextPage_ReturnsTrueWhenTruncatedAndRequestIdsAreCoherent()
{
    var state = new MarketBoardPaginationState(
        itemId: 5064,
        worldName: "Siren",
        reportedListingCount: 180,
        readableListingCount: 100,
        listingCapacity: 100,
        currentRequestId: 1,
        nextRequestId: 2);

    Assert.True(state.CanRequestNextPage);
}

[Fact]
public void IsContinuationOf_RejectsDifferentItem()
{
    var first = new MarketBoardPaginationState(5064, "Siren", 180, 100, 100, 1, 2);
    var next = new MarketBoardPaginationState(2, "Siren", 80, 80, 100, 2, 3);

    Assert.False(next.IsContinuationOf(first));
}
```

- [x] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPaginationStateTests" -v minimal
```

Expected: fail because the new model does not exist.

- [x] **Step 3: Implement `MarketBoardPaginationState`**

Add:

```csharp
namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardPaginationState(
    uint ItemId,
    string WorldName,
    int ReportedListingCount,
    int ReadableListingCount,
    int ListingCapacity,
    byte CurrentRequestId,
    byte NextRequestId)
{
    public bool IsTruncated =>
        ReportedListingCount > ReadableListingCount &&
        ListingCapacity > 0 &&
        ReadableListingCount >= ListingCapacity;

    public bool HasCoherentRequestIds => NextRequestId != CurrentRequestId;

    public bool CanRequestNextPage => IsTruncated && HasCoherentRequestIds;

    public bool IsContinuationOf(MarketBoardPaginationState previous) =>
        ItemId == previous.ItemId &&
        WorldName.Equals(previous.WorldName, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 4: Run focused tests**

Run the same `dotnet test` command and confirm all pagination state tests pass.

## Task 3: Add A Non-Purchasing Pagination Probe

Status: Partial. The pure classifier is complete in `MarketMafioso/MarketAcquisition/MarketBoardPaginationProbe.cs` with tests in `MarketMafioso.Tests/MarketAcquisition/MarketBoardPaginationProbeTests.cs`, and the diagnostics window now displays the probe classification plus a disabled next-page probe control for the current read result. Any live page-request attempt remains intentionally unimplemented until Task 1 captures prove the safe request transition.

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPaginationProbe.cs`
- Modify: `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPaginationProbeTests.cs`

- [x] **Step 1: Write tests for probe refusal**

Tests should prove the probe refuses:

- no active item search,
- untruncated reads,
- unchanged request ids after a probe attempt,
- changed item id after a probe attempt.

- [x] **Step 2: Implement the probe as diagnostics-only**

The first implementation must not purchase or advance the route. It may call a proved request method only after Task 1 capture evidence identifies the correct method and field transition. If Task 1 only proves that no safe request method is available, implement the probe as a refusal result that explains why deeper pagination remains unavailable.

The probe result should include:

```csharp
public sealed record MarketBoardPaginationProbeResult
{
    public string Status { get; init; } = "Unavailable";
    public string Message { get; init; } = string.Empty;
    public MarketBoardPaginationState? Before { get; init; }
    public MarketBoardPaginationState? After { get; init; }
}
```

- [x] **Step 3: Surface probe result in diagnostics**

Add a diagnostics-window button labeled `Probe Next Listing Page`. Show the result under the live market-board diagnostics. The control currently stays disabled and explains that live page requests are gated on capture evidence.

- [x] **Step 4: Verify no route behavior changes**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPagination|FullyQualifiedName~MarketAcquisitionRouteRunnerTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests" -v minimal
```

Expected: existing route and candidate tests still pass.

## Task 4: Merge Additional Pages Into Live Candidate Planning

Status: Pure model/planner support complete. `MarketBoardAccumulatedReadResult` safely merges unique visible listings from multiple reads for the same item/world, rejects mixed item/world reads, preserves reported listing counts and request ids, and can be fed into live candidate planning. Actual live page requesting remains blocked on Task 1 capture evidence.

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardListingReader.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveCandidatePlanner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionLiveCandidatePlannerTests.cs`

- [x] **Step 1: Add accumulated-read model tests**

Write tests proving two reads for the same item/world merge by listing id and reads for different item/worlds are rejected.

- [x] **Step 2: Implement accumulated reads**

Add a model that preserves:

- all unique visible listing rows,
- total reported listing count,
- listing capacity per page,
- page count read,
- whether the final result is still truncated.

- [x] **Step 3: Feed accumulated reads into candidate planning**

When a line has accumulated reads, candidate planning should evaluate all accumulated listings. If the accumulated read is still truncated and no safe candidate was found, keep status `VisibleCacheExhausted`.

- [x] **Step 4: Verify candidate behavior**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests|FullyQualifiedName~MarketBoardListingReaderTests" -v minimal
```

Expected: candidate planner distinguishes ordinary no-stock from visible-cache exhaustion and can select candidates from later accumulated pages.

Status: Complete. Focused verification passed with 33 tests:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPagination|FullyQualifiedName~MarketBoardListingReaderTests|FullyQualifiedName~MarketBoardAccumulatedReadResultTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests" -v minimal
```

## Task 5: Live Validation Gate

**Files:**
- Modify: `docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md`
- Modify: `docs/design/2026-06-28-market-acquisition-next-feature-list.md`

- [x] **Step 1: Deploy the diagnostic build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

- [ ] **Step 2: Live test on an item with more than 100 listings**

Run route diagnostics on an item where `infoProxyReportedListingCount > 100`.

Passing evidence:

- route log shows page 1 read,
- pagination probe advances to another coherent page or explicitly refuses with a proven reason,
- if a safe listing exists only beyond the first readable page, candidate planning sees it before purchasing,
- if no deeper page can be requested, route line ends as `SkippedVisibleCacheExhausted` with counts.

- [ ] **Step 3: Update status docs**

If live pagination succeeds, move deeper pagination out of Deferred and into Phase 5 notes as implemented. If it does not, keep it deferred and paste the exact refusal reason/evidence into both docs.

## Verification Commands

Use focused tests while iterating:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPagination|FullyQualifiedName~MarketBoardListingReaderTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests" -v minimal
```

Before committing code:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Before live handoff:

```powershell
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```
