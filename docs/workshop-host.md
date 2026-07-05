# Workshop Host

Workshop Host is the suite-level name for the private self-hosted backend that powers optional MarketMafioso and future Craft Architect integrations.

The current MarketMafioso server package is still named the **receiver** in code, scripts, routes, and release bundles. That wording remains accurate for its first job: receiving inventory reports from the plugin. In user-facing architecture docs, treat it as the first Workshop Host runtime.

## What Requires Workshop Host

Use Workshop Host when you want private state or automation:

- persistent inventory snapshots;
- browser dashboard sessions;
- diagnostics and stored report history;
- receiver-backed Market Acquisition queues;
- Craft Architect quote lookup for MMF's Craft Architect Companion;
- future cross-tool automation between Craft Architect and MarketMafioso.

## What Does Not Require Workshop Host

MarketMafioso should remain useful without Workshop Host:

- Workshop Logistics;
- local queue preparation;
- Craft Architect Companion manual craft-cost evidence;
- Craft Architect Companion quote-file imports;
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

## Machine Scopes

Workshop Host routes are classified with named machine scopes:

- `inventory:write`
- `inventory:read`
- `craft:quote`
- `acquisition:queue`
- `diagnostics:read`
- `automation:run`

Current self-host builds keep `MarketMafioso__ClientApiKey` as the compatibility key for all implemented non-dashboard machine scopes. The scope names exist so quote evidence, acquisition queues, diagnostics, and future automation can be separated without changing the route model later.

## Public Service Boundary

Do not treat the public Craft Architect VPS as the dependency boundary for MarketMafioso. Public services should stay small, cacheable, and non-user-specific.

Workshop Host is the right place for user-specific data, authenticated APIs, and automation because the operator owns the storage, cost, uptime, and trust boundary.

## Transitional Naming

Use these terms in docs:

- **Workshop Host**: the self-hosted suite backend tier.
- **receiver**: the current MarketMafioso server/runtime that ingests inventory and hosts the dashboard.
- **hosted receiver**: the current dev VPS deployment of that receiver runtime.

Future code/package renames can happen after the contract and handoff seams settle.
