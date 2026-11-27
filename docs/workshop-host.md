# Workshop Host

Workshop Host is the suite-level name for the private self-hosted backend that powers optional MarketMafioso and future Craft Architect integrations.

The current MarketMafioso server package is still named the **receiver** in code, scripts, routes, and release bundles. That wording remains accurate for its first job: receiving inventory reports from the plugin. In user-facing architecture docs, treat it as the first Workshop Host runtime.

## What Requires Workshop Host

Use Workshop Host when you want private state or automation:

- persistent inventory snapshots;
- browser dashboard sessions;
- Market Acquisition work orders queued from the dashboard or Craft Architect;
- diagnostics and stored report history;
- Craft Architect quote lookup for Acquisition Workbench craft appraisal;
- future private cross-tool state between Craft Architect and MarketMafioso.

## What Does Not Require Workshop Host

MarketMafioso should remain useful without Workshop Host:

- Workshop Logistics;
- local queue preparation;
- Market Acquisition buy lists, plans, and buying routes;
- manual craft-cost evidence for Acquisition Workbench appraisal;
- Craft Architect quote-file imports for Acquisition Workbench appraisal;
- local quick-shop route preparation.

Craft Architect should also remain local-first. Exported quote files are the first CA/MMF handoff path because they do not require a public API, a local server process, or a self-hosted backend.

## Capability Discovery

Workshop Host exposes a machine-readable capabilities endpoint:

```text
GET /api/capabilities
```

MMF uses this endpoint before enabling hosted Craft Architect quote lookup. Workshop Host advertises `craft.appraise` whenever the calling key has the `craft:quote` scope. If it is absent, MMF keeps the quote action unavailable instead of silently substituting another computation path.

## Craft Quote API

Workshop Host reserves the private craft appraisal route:

```text
POST /api/craft/appraise
```

The route requires the client API key when API-key auth is enabled. Workshop Host runs Craft Architect's appraisal engine in-process (the `Gooseworks.FFXIVCraftArchitect.Core` package) and maps it onto the same versioned wire contract. Error responses carry an `error` code and a `detail` sentence naming what was missing or which limit tripped. MMF treats quote API failures as visible evidence-provider failures, not as a silent fallback to stale or manual costs.

A quote's `planUrl` opens the Craft Architect web app, which reads the priced plan back from Workshop Host:

```text
GET /api/craft/plans/{planId}
```

The plan id is the SHA-256 of the snapshot (64 hex characters) and is the only credential: the route needs no API key or dashboard session, returns 404 for any other id shape, and allows cross-origin `GET` only from `MarketMafioso__CraftArchitectAppOrigin`, including the private-network preflight a public HTTPS page sends to a `localhost` host.

Dashboard item search (`GET /api/xivdata/items/search?q=&limit=`) is served from the same engine's item data provider.

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
