# Market Acquisition Next Feature List

## Purpose

This is the lightweight holding document for the next Market Acquisition work path after the multi-item execution pass. It is not yet an implementation plan. It records candidate features, the intended order of investigation, and what each feature needs to prove before it graduates into a design or implementation plan.

## Active Context

The acquisition loop can now stage batches, claim them in the plugin, prepare route plans, travel, search the market board, read live listings, and buy safe listings. The next useful work should make the loop easier to trust and easier to diagnose while avoiding fake authority over market data.

The normal Universalis update path remains XIVLauncher/Dalamud observing real market-board activity. MarketMafioso should not spoof or partially upload current-listing data. It should verify that normal uploads eventually reflected the market-board state it caused.

## Current Implementation Notes

- Same-world multi-item execution now resets market-board search, candidate, read, and purchase state when advancing from one route line to the next on the same world.
- The market-board listing reader consumes the full visible `InfoProxyItemSearch` cache and reports when the game reports more listings than that readable cache exposes. This is diagnostic coverage for truncation, not proof of deeper pagination support.
- Universalis freshness verification is wired as post-world diagnostic evidence. Unconfirmed or unavailable freshness checks produce loud post-run warnings but do not block route progress.
- Planner diagnostics now include per-listing decisions for hard filters, quantity caps, gil caps, and explicit sweep-probe worlds. The diagnostics window exposes these in a `Plan Decisions` table.
- The dashboard acquisition board now defaults to active requests, has an archive view for terminal batches, and can reuse prior one-line or multi-line batches as composer drafts with `Run again`.
- Server-side route-log indexing remains future convenience work. The dashboard settings diagnostics view explicitly notes that detailed route logs are currently client-local until the plugin uploads sanitized route summaries.

## Paper Stack

This document is the active holding list for the next feature track. It should not replace the existing acquisition design docs:

- `2026-06-25-market-acquisition-roadmap.md` is historical for the single-item proof track and points at the active follow-up tracks.
- `2026-06-28-market-acquisition-multi-item-batches.md` is the durable model/spec for batch, line, attempt, route stop, and subtask semantics.
- `2026-06-28-market-acquisition-multi-item-roadmap.md` is the active roadmap for finishing multi-item execution.
- This document is the queue of follow-up features after the multi-item foundation, plus investigation notes for features that may need to feed back into that roadmap.

When a feature here depends on unfinished multi-item foundation work, it should say so explicitly instead of pretending the dependent data already exists.

## Recommended Order

1. Finish the multi-item foundation that affects correctness:
   - per-line progress and audit payloads,
   - line-aware route diagnostics,
   - stable route/log identifiers.
2. Build trust surfaces on top of that data:
   - claimed batch line-aware UI,
   - per-world completion summaries,
   - plan explainability and route optimizer transparency.
3. Add exploratory acquisition behavior:
   - opportunistic full-batch world checks,
   - scoped all-world sweep validation and polish.
4. Add ecosystem verification:
   - Universalis freshness verifier.
5. Add convenience surfaces:
   - sortable advisory tables,
   - route log index,
   - completed batch archive and run-again presets.

This order is not strict when a small UI improvement is cheap, but features that claim facts about purchases should wait for line-aware progress/audit data.

## Investigation Checklist

Before turning this feature list into a new implementation plan, verify these seams:

- Line state source of truth:
  - Server and shared DTOs already carry batch lines with purchased quantity, spent gil, status, and latest message.
  - The current route session mostly tracks purchased quantity and gil at the world-stop level.
  - Any UI that displays per-line purchase results needs explicit line-level progress or purchase audit events first.
- Universalis freshness:
  - Existing Universalis plan source reads `listings` and `lastReviewTime` per listing.
  - A freshness verifier likely needs a separate response parser that can inspect upload/freshness metadata, not just the current advisory listing projection.
  - Verification should happen after each world batch, not after each item line.
- Opportunistic world checks:
  - Planner already carries item subtasks per world.
  - Runner can advance through item subtasks on a world.
  - The missing detail is how to add non-planned line checks without confusing planned-vs-opportunistic diagnostics or route totals.
- Scoped all-world sweep:
  - The first implementation slice exists, but it still needs live/dashboard validation before treating it as complete.
  - Probe-only stops must remain visible as probe-only, not as failed or zero-value economic recommendations.
- Sortable advisory plan:
  - Sorting is a view concern only.
  - Route execution order must remain the stored plan order, regardless of table sort.
  - Diagnostics should reference stable world and line identifiers, not visible table row index.

## Feature 1: Universalis Freshness Verifier

### Goal

Add a small reusable service that can answer: "Does Universalis appear fresh enough for this item on this world after we interacted with the market board?"

### Foundation Dependency

This should wait until per-world completion boundaries are reliable enough to know exactly which `world + item` pairs were observed or purchased during the stop. It does not require per-line audit to exist first, but it becomes more useful once purchase audit can provide listing ids that should disappear.

### Intended Scope

- Verify freshness for one `world + itemId` at a time.
- Run checks at the end of a world purchase batch for every item touched on that world.
- Treat freshness failure as diagnostic unless explicitly promoted to a hard route failure later.
- Reuse the service anywhere MarketMafioso needs to confirm that public market data caught up after an in-game observation.

### Candidate Inputs

- World id or world name.
- Item id.
- Route attempt id.
- Optional line id.
- The local observation time after the final post-purchase listing read.
- Purchased or observed listing ids when available.
- A timeout and polling interval.

### Candidate Outputs

- `Confirmed`: Universalis looks updated after the local observation.
- `Pending`: a check is still within its timeout window.
- `Unconfirmed`: timeout elapsed without convincing evidence.
- `Unavailable`: Universalis request failed or returned an unusable shape.

### Evidence Rules

Preferred confirmation:

- Universalis `lastUploadTime` or equivalent freshness timestamp is after the local post-purchase observation.

Secondary confirmation:

- Listings known to be purchased or exhausted are no longer present in Universalis current listings after the local post-purchase observation.

Non-confirmation:

- Universalis still shows stale listing ids or has not advanced freshness metadata.

### Diagnostics

The verifier should write compact, human-readable diagnostics:

- check start: item, world, attempt, line, observation time,
- each poll result: HTTP status, freshness timestamp, relevant listing ids seen/missing,
- terminal result: confirmed, unconfirmed, unavailable, elapsed time.

### Open Questions

- Whether the verifier should live purely plugin-side first, or whether the server should expose a MarketMafioso API wrapper later for dashboard inspection.
- Exact timeout default for live routes.
- Whether `Unconfirmed` should ever block moving to the next world.
- Which Universalis response fields are reliable enough for freshness confirmation beyond listing-level `lastReviewTime`.

## Feature 2: Per-World Completion Summary

### Goal

Make each world stop end with a concise summary that explains what happened before the route moves on.

### Intended Scope

- Summarize all item lines touched on the current world.
- Include purchased quantity, gil spent, skipped/under-procured lines, and remaining route work.
- Include Universalis freshness status once Feature 1 exists.
- Show the summary in plugin status and route diagnostics.
- Later, mirror the summary into dashboard attempt history.

### Foundation Dependency

World-level totals exist today, but line-level purchased/skipped status is still incomplete. First useful version can summarize world totals and active item messages; trustworthy multi-item summaries need line-aware progress/audit from the multi-item roadmap.

### Example Summary Shape

```text
Maduin complete: bought 4 line(s), 612 item(s), spent 318,400 gil.
Freshness: confirmed for 3 item(s), unconfirmed for 1 item.
Next: Seraph, 2 line(s), 180 planned item(s).
```

### Value

This gives the user a clean checkpoint between worlds, makes multi-item routes easier to follow, and gives diagnostics a natural boundary for later replay.

## Feature 3: Opportunistic Full-Batch World Checks

### Goal

When a route reaches a world, check every item line in the active procurement batch on that world's market board, not only the lines that remote advisory data already believed were promising there.

### Rationale

If MarketMafioso is already standing at a market board, the marginal cost of checking the rest of the batch is mostly time. The upside is meaningful:

- normal XIVLauncher/Dalamud market-board uploads can refresh Universalis for every checked item,
- stale remote data can be corrected by live market-board truth,
- surprise cheap listings can be discovered and purchased,
- missed remote candidates become less costly,
- multi-item routes feel less brittle because every world visit becomes a useful scan opportunity.

### Intended Scope

- Apply only to items already present in the active acquisition batch.
- At each world stop, search/read every unfinished line in dashboard queue order.
- Preserve the planned route order; this feature expands item checks within a world, not world selection.
- Buy only from confirmed live market-board listings that satisfy that line's constraints.
- Respect remaining line quantity, optional max quantity, optional gil cap, max unit price, and HQ policy.
- Treat no safe listings as normal per-line exhaustion for that world, not a failure.
- Record whether a line was `planned-for-world` or `opportunistic-check` for diagnostics and future dashboard explanation.

### Foundation Dependency

This depends on the route runner being able to process multiple item subtasks on one world and clear stale item-search/listing state between subtasks. That path exists, but opportunistic checks should add explicit planned-vs-opportunistic classification before changing route behavior.

### Execution Shape

```text
For each world stop:
  1. Check planned item subtasks first.
  2. Check remaining unfinished batch lines that were not planned for this world.
  3. Fold any safe live listings into the same purchase loop.
  4. Summarize planned buys, opportunistic buys, and no-safe-stock checks before leaving the world.
```

### Diagnostics

The route log should distinguish:

- remote planned candidate,
- live planned candidate,
- opportunistic live candidate,
- checked but no safe stock,
- checked but search/read failed,
- skipped because the line was already complete.

### Open Questions

- Whether opportunistic checks should always run, or be a route mode toggle later if the time cost becomes annoying.
- Whether a world with only opportunistic wins should be eligible for future route reordering in the same run.
- Whether dashboard summaries should count opportunistic purchases separately from planned purchases.
- Whether opportunistic no-stock checks should be included in Universalis freshness verification.

## Feature 4: Scoped All-World Sweep

### Goal

Make `All-world sweep` a real route mode rather than a label on recommended routing, and allow the user to choose the sweep boundary.

### Current Status

First implementation slice exists in the planner, server contract, and dashboard request builder. It still needs live/dashboard validation and route-diagnostics polish before it should be considered done.

### Intended Scope

- `Recommended worlds` remains the normal economic mode: plan from remote listings that already satisfy the request constraints.
- `All-world sweep` explicitly creates a route stop for every world in the selected scope, even if remote data has no supported listings there.
- Sweep route stops include probe-only item subtasks when no advisory listing exists, allowing the plugin to search live market-board data and buy surprise safe stock.
- Supported sweep scopes:
  - entire North America region,
  - current data center,
  - selected North America data centers.
- Selected data centers allow practical sweeps like "Dynamis only" or "Dynamis + Crystal" without forcing a full regional route.

### Behavioral Contract

- Planned quantity and gil remain advisory totals from remote listings.
- Probe-only worlds have zero planned quantity/gil, but are still valid route stops.
- Live market-board validation remains authoritative for purchase decisions.
- No safe stock on a probe-only world is a normal outcome, not a route failure.
- Quantity caps and gil caps can suppress advisory planned listings, but they should not silently remove probe stops from an explicit sweep.

### Foundation Dependency

Sweep relies on route stops that may contain no advisory listings. Runner and diagnostics must treat those as intentional probe-only stops. Any plan UI must avoid presenting probe-only stops as economic recommendations.

### Diagnostics

Route diagnostics should make it obvious when a stop is:

- remote-planned,
- scoped-sweep/probe-only,
- scoped-sweep with advisory listings,
- checked with no safe stock.

## Feature 5: Claimed Batch Line-Aware UI

### Goal

Make the in-game claimed request surface accurately represent multi-item acquisition batches.

### Foundation Dependency

The batch/line DTOs already exist, so the static claimed-batch table can be improved immediately. Purchased quantity, spent gil, line status, and latest line message should only be shown as authoritative once per-line progress/audit events are implemented and projected back into the claim view.

### Rationale

The current `Claimed Request` table still behaves like a single-item summary. It shows fields such as item, mode, quantity, max unit, gil cap, and HQ policy as if the whole claimed unit has one value for each. In the multi-item model, those are line-level values. Keeping them in one key-value summary makes a real batch look like one arbitrary line won.

### Intended Scope

- Rename the main section from `Claimed Request` to `Claimed Batch`.
- Show a compact batch summary for fields that truly apply to the whole batch:
  - status,
  - target character/world,
  - line count,
  - routing mode,
  - sweep scope when applicable,
  - latest batch-level message.
- Add one dense line table with one row per item line:
  - item,
  - mode,
  - max unit,
  - max quantity,
  - gil cap,
  - HQ policy,
  - planned quantity/gil,
  - purchased quantity/gil,
  - line status.
- Keep accept, reject, forget, prepare, and route controls batch-level.
- Treat one-line batches as the same UI shape rather than a special case.
- Move noisy line diagnostics, market-board reads, and purchase attempt details into the diagnostics window.

### Minimum Useful Slice

- Batch summary table.
- Line table showing item, mode, max unit, max quantity, gil cap, HQ policy, and planned totals.
- Clearly muted placeholders for purchased/spent/status when server-side line projections are not yet live.

### Behavioral Contract

- One under-procured or skipped line should not make the whole batch look failed without context.
- The UI should make it obvious which item line is currently active during route execution.
- The summary should never display line-level values as if they are batch-level values.

### Value

This makes multi-item execution easier to trust: the user can see the batch as one unit of work while still understanding each item's state.

## Feature 6: Sortable Advisory Plan Table

### Goal

Make advisory plan tables sortable so the user can inspect planned route data by the dimension that matters in the moment.

### Foundation Dependency

This is mostly independent. It should be scoped to the plugin advisory-plan display and must not mutate the route session, plan object, active stop index, or execution order.

### Intended Scope

- Add sorting to the advisory plan/world listings table in the plugin UI.
- Preserve the route execution order as the authoritative route order; table sorting is inspection-only.
- Sort useful columns such as:
  - world,
  - data center,
  - planned quantity,
  - planned gil,
  - live quantity,
  - live gil,
  - dry-run or candidate status when present.
- Default sort remains route order.
- Sorting should not mutate the stored route plan, current route index, or purchase execution order.

### Diagnostics

If diagnostics or route logs reference table rows, they should continue to reference stable world/line identifiers rather than visible row index. Sort order is a view concern only.

### Value

Sorting helps catch route-planning oddities, inspect expensive worlds, and compare same-data-center stops without turning the advisory table into a separate planning engine.

## Candidate Follow-Ups

- Plan explainability view: show why remote listings were accepted or rejected, especially for `NoSupportedListings` cases where CA or Universalis appears to show plenty of stock.
- Route optimizer transparency: show why worlds were ordered the way they were, including current-world priority, data-center grouping, planned gil, planned quantity, and sweep/probe-only stops.
- Dashboard attempt detail timeline for world summaries and freshness checks.
- Route log index view in the dashboard, with searchable logs and clearer route/input-capture filenames.
- Completed batch archive and `Run again` preset flow for repeated procurement needs.
- Inventory and gil readiness warnings before launch, kept advisory unless the route would obviously be impossible.
- Optional route time-cost estimates once all-world sweep and opportunistic checks are stable enough to make ETA meaningful.
- Retainer-listing overlay for MarketMafioso-owned knowledge about the user's own listings.
- Better stale-data warnings in dashboard planning when CA/MMF/Universalis disagree.

## Non-Goals For This Pass

- Direct Universalis uploads from MarketMafioso.
- Hijacking XIVLauncher or Dalamud internal upload services.
- Treating retainer-bell listing data as public current-listing data.
- Blocking purchases on Universalis freshness by default.
- Replacing live market-board validation with remote data.
