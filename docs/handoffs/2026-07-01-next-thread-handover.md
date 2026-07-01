# MarketMafioso Next Thread Handover

## Current State

MarketMafioso has just been cleaned into a clearer repository layout:

- `src/MarketMafioso/` contains the Dalamud plugin.
- `src/MarketMafioso.Server/` contains the self-hosted receiver/backend.
- `src/MarketMafioso.Dashboard/` contains the Blazor dashboard.
- `tests/MarketMafioso.Tests/` contains plugin tests.
- `tests/MarketMafioso.Server.Tests/` contains server tests.

The layout commit was pushed to `main` as:

- `cbd0888 chore: consolidate source and test layout`

The project is at a natural checkpoint after the market acquisition prototype/refactor work, dashboard rebuild work, and self-hosting packaging pass.

## Product Direction

The next development phase should split into two tracks:

1. Public-safe product work.
2. Private/internal market acquisition work.

Market Acquisition is powerful but sits in a gray area for public plugin presentation. It should be hidden or feature-gated by default before any broadly usable release. Inventory Reporter and Workshop Logistics should remain the visible first-class public features.

## Recommended Next Pass

The strongest next pass is:

1. Hide or feature-gate Market Acquisition by default.
2. Make Inventory Reporter and Workshop Logistics feel release-worthy.
3. Keep Market Acquisition available for internal/private testing behind an explicit opt-in.

This gives MarketMafioso a safe public surface while preserving the acquisition machinery for private use and continued development.

## Public-Safe Track

### 1. Hide Market Acquisition By Default

Market Acquisition should not appear in the normal plugin module list or public dashboard navigation unless an explicit internal/dev setting is enabled.

Suggested behavior:

- Public/default config hides the Market Acquisition tab and related dashboard views.
- Existing internal config can enable it.
- Hidden mode should not delete existing market acquisition data or settings.
- Any server endpoints required for private use can remain available behind auth, but should not be advertised in the default UI.

### 2. Inventory Dashboard Completion

The backend now has structured inventory storage, but the viewer still deserves a full product pass.

Potential work:

- Stronger item search/filter UX.
- Character and retainer scope filters.
- Retainer gil shown clearly.
- Retainer market listings displayed separately from inventory holdings.
- Item type, HQ, quantity, owner, and location data presented in dense but readable tables.
- Raw snapshot diagnostics moved out of primary navigation.

### 3. Workshop Logistics Polish

Workshop Logistics is one of the safe first-class modules and should feel complete.

Potential work:

- Better queue review before execution/export.
- Clearer retainer availability and missing-material explanation.
- Better VIWI/Artisan handoff affordances.
- Cleaner success/failure summaries.
- Documentation for the intended workflow.

### 4. Self-Hosting Package

The backend should be reasonably simple for a technically capable friend to run, without turning it into a public SaaS.

Potential work:

- Publish prebuilt Docker images.
- Keep `compose.yaml` and setup/update scripts current.
- Add release notes explaining setup, update, API key configuration, and dashboard login.
- Verify the documented flow from a clean checkout or release artifact.

## Private/Internal Acquisition Track

### 1. Runner State-Machine Follow-Through

The runner is now much more stable, but the module still deserves structural cleanup.

Refactor goals:

- Route, world, item, listing, and purchase states should be explicit.
- Avoid giant orchestration blocks in UI/window classes.
- Keep UI automation helpers reusable and isolated.
- Keep diagnostics generation separate from route decision logic.
- Preserve the current working behavior while improving maintainability.

### 2. Post-Run Intelligence

Successful large runs showed that opportunistic checks are very valuable. The plugin should make that value visible.

Potential reports:

- Per-world purchased quantity and gil spent.
- Planned vs discovered vs bought by item.
- Opportunistic purchases and savings.
- Missed or skipped opportunities.
- Universalis freshness warnings.
- Post-run summary suitable for quick review.

### 3. Acquisition Dashboard Usability

The rebuilt dashboard is cleaner, but acquisition still needs workflow polish.

Potential work:

- Multi-item request editing before pickup.
- Request merge support for unclaimed requests.
- Persistent drafts through reload.
- Completed/failed request archive.
- Reuse old requests as presets.
- Better active queue status and diagnostics surfacing.

### 4. Sanitized Purchase Guide Mode

Longer term, a public-safe acquisition-adjacent mode could exist without automated buying.

Possible scope:

- Route planner.
- World/item checklist.
- Manual purchase guide.
- Live validation helpers.
- No automated market board purchases.

This could let some of the acquisition planning value become public without exposing the private automation surface.

## Immediate Priority Recommendation

Start with:

1. Feature-gate Market Acquisition from the default public UI.
2. Finish the Inventory and Workshop surfaces enough that the visible plugin feels coherent.
3. Keep internal Market Acquisition work moving in smaller, testable slices.

This balances release safety, user-facing polish, and continued progress on the internal acquisition system.
