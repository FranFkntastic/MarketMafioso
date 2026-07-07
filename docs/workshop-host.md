# Workshop Host

Workshop Host is the suite-level name for the private self-hosted backend that powers optional MarketMafioso and future Craft Architect integrations.

The current MarketMafioso server package is still named the **receiver** in code, scripts, routes, and release bundles. That wording remains accurate for its first job: receiving inventory reports from the plugin. In user-facing architecture docs, treat it as the first Workshop Host runtime.

## What Requires Workshop Host

Use Workshop Host when you want private state or automation:

- persistent inventory snapshots;
- browser dashboard sessions;
- diagnostics and stored report history;
- Craft Architect quote lookup for Acquisition Workbench craft appraisal;
- future private cross-tool state between Craft Architect and MarketMafioso.

## What Does Not Require Workshop Host

MarketMafioso should remain useful without Workshop Host:

- Workshop Logistics;
- local queue preparation;
- manual craft-cost evidence for Acquisition Workbench appraisal;
- Craft Architect quote-file imports for Acquisition Workbench appraisal;
- local quick-shop route preparation.

Craft Architect should also remain local-first. Exported quote files are the first CA/MMF handoff path because they do not require a public API, a local server process, or a self-hosted backend.

## Capability Discovery

Workshop Host exposes a machine-readable capabilities endpoint:

```text
GET /api/capabilities
```

MMF uses this endpoint before enabling Workshop Host craft quote lookup. Current source builds advertise `craft.appraise` by default because the receiver directly references Craft Architect Core for appraisal. If an older or custom host does not advertise `craft.appraise`, MMF skips the Workshop Host quote provider and continues to quote-file or manual evidence.

## Craft Quote API

Workshop Host reserves the private craft appraisal route:

```text
POST /api/craft/appraise
```

The route requires the client API key when API-key auth is enabled. The current receiver implementation delegates directly to Craft Architect Core. MMF treats configured quote API failures as visible evidence-provider failures, not as a silent fallback to stale or manual costs.

MMF keeps an in-memory last-good quote cache per appraisal signature while the plugin session is alive. If live quote evidence fails, the cached quote is labeled with `(last-good)` and warning text. Route creation still uses the user's explicit buy threshold, not the cached quote cost.

## Machine Scopes

Workshop Host routes are classified with named machine scopes:

- `inventory:write`
- `inventory:read`
- `craft:quote`
- `diagnostics:read`

Current self-host builds keep `MarketMafioso__ClientApiKey` as the compatibility key for all implemented non-dashboard machine scopes. The scope names exist so inventory, quote evidence, diagnostics, and future private integrations can be separated without changing the route model later.

Optional scoped settings are available for narrower clients: `InventoryWriteApiKey`, `InventoryReadApiKey`, `CraftQuoteApiKey`, and `DiagnosticsReadApiKey`. Any scoped machine key may read `/api/capabilities`; feature routes still require their matching scope.

## Public Service Boundary

Do not treat the public Craft Architect VPS as the dependency boundary for MarketMafioso. Public services should stay small, cacheable, and non-user-specific.

Workshop Host is the right place for user-specific data, authenticated APIs, and automation because the operator owns the storage, cost, uptime, and trust boundary.

## Transitional Naming

Use these terms in docs:

- **Workshop Host**: the self-hosted suite backend tier.
- **receiver**: the current MarketMafioso server/runtime that ingests inventory and hosts the dashboard.
- **hosted receiver**: the current dev VPS deployment of that receiver runtime.

Future code/package renames can happen after the contract and handoff seams settle.
