# Workshop Host

Workshop Host is the suite-level name for the private self-hosted backend that powers optional MarketMafioso and future Craft Architect integrations.

The current MarketMafioso server package is still named the **receiver** in code, scripts, routes, and release bundles. That wording remains accurate for its first job: receiving inventory reports from the plugin. In user-facing architecture docs, treat it as the first Workshop Host runtime.

## What Requires Workshop Host

Use Workshop Host when you want private state or automation:

- persistent inventory snapshots;
- browser dashboard sessions;
- diagnostics and stored report history;
- receiver-backed Market Acquisition queues;
- future cross-tool automation between Craft Architect and MarketMafioso.

## What Does Not Require Workshop Host

MarketMafioso should remain useful without Workshop Host:

- Workshop Logistics;
- local queue preparation;
- Craft Architect Companion manual craft-cost evidence;
- Craft Architect Companion quote-file imports;
- local quick-shop route preparation.

Craft Architect should also remain local-first. Exported quote files are the first CA/MMF handoff path because they do not require a public API, a local server process, or a self-hosted backend.

## Public Service Boundary

Do not treat the public Craft Architect VPS as the dependency boundary for MarketMafioso. Public services should stay small, cacheable, and non-user-specific.

Workshop Host is the right place for user-specific data, authenticated APIs, and automation because the operator owns the storage, cost, uptime, and trust boundary.

## Transitional Naming

Use these terms in docs:

- **Workshop Host**: the self-hosted suite backend tier.
- **receiver**: the current MarketMafioso server/runtime that ingests inventory and hosts the dashboard.
- **hosted receiver**: the current dev VPS deployment of that receiver runtime.

Future code/package renames can happen after the contract and handoff seams settle.
