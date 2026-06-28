# MarketMafioso Dashboard Rebuild Design

## Goal

Replace the current server-rendered MarketMafioso dashboard with a Craft Architect-style Blazor WebAssembly + MudBlazor app backed by explicit JSON APIs, cookie-backed dashboard sessions, and persistent diagnostics.

The existing dashboard should be treated as throwaway UI. Its current acquisition, inventory, diagnostics, and snapshot behavior remains useful as product evidence, but the implementation model should not be preserved.

## Locked Decisions

- Rebuild the dashboard instead of patching the current interpolated HTML pages.
- Use Blazor WebAssembly and MudBlazor to align with Craft Architect frontend patterns.
- Default the app to Acquisition because acquisition testing is the current blocking workflow.
- Put Diagnostics and Snapshots under Settings rather than primary top-level tabs.
- Use server-sent events for live dashboard updates now.
- Replace Basic Auth dashboard prompts with cookie-backed login sessions.
- Do not reintroduce dashboard CSRF tokens in this private/self-hosted receiver pass.
- Keep plugin API key auth for plugin-to-server ingest, pickup, progress, and completion calls.
- Persist diagnostics aggressively, targeting about 5000 events.
- Store diagnostic evidence broadly, while redacting secrets and avoiding duplicate full inventory raw JSON.

## Browser Debugging Model

Moving the dashboard to Blazor restores the useful browser-debug path that Craft Architect has for client-side behavior:

- client-side exceptions can surface in browser devtools,
- failed fetches can be inspected as normal network calls,
- dashboard UI state transitions can log to the browser console,
- component-level behavior is no longer hidden inside server-rendered string output.

This does not replace server diagnostics. Acquisition staging, plugin pickup, route progress, purchase reporting, inventory ingest, SQLite writes, and hosted deployment failures still happen outside the browser. Those flows need persistent server-side diagnostic events and live SSE updates.

## Project Shape

Add a new client project:

```text
MarketMafioso.Dashboard/
```

Recommended shape:

```text
MarketMafioso.Dashboard/
  MarketMafioso.Dashboard.csproj
  Program.cs
  App.razor
  Routes.razor
  Layout/
  Pages/
    Acquisition/
    Inventory/
    Overview/
    Settings/
  Components/
  Services/
  Models/
  wwwroot/
```

The server project remains the deployment host:

```text
MarketMafioso.Server/
```

It should serve the Blazor static assets and expose JSON/SSE endpoints. The server keeps SQLite, acquisition stores, inventory stores, auth/session services, and diagnostics stores.

## Navigation And IA

Top-level app navigation:

1. Acquisition
2. Inventory
3. Overview
4. Settings

Settings sections:

- General
- Diagnostics
- Snapshots
- Authentication
- Server

Diagnostics and snapshots are important, but they should not compete with the operational acquisition surface. They are tools for explaining the system, not the primary reason the user opened the dashboard.

## Acquisition Surface

The Acquisition page should be rebuilt around the actual testing and execution workflow:

- left panel: request builder and plan queue,
- center/right panel: active queue and selected request details,
- lower or side panel: live plugin status and latest route/purchase progress,
- clear stage/claim/run lifecycle states,
- explicit `AllBelowThreshold` semantics in both dashboard and plugin status display.

Quantity modes:

- `TargetQuantity`
- `AllBelowThreshold`

Removed modes:

- `Exact`
- `UpTo`

For `AllBelowThreshold`, the quantity field should become `Max quantity` and may be blank. Blank means no quantity cap. Gil cap remains optional. Max unit price remains mandatory.

The acquisition queue should update through SSE rather than manual refresh or polling. Actions such as cancel and resend should update the visible queue immediately once the server accepts them.

## Cookie Session Auth

Replace dashboard Basic Auth prompts with app-managed cookie sessions.

Required routes:

```http
POST /auth/login
POST /auth/logout
GET /auth/session
```

These routes are relative to the dashboard base path. On hosted dev that means `/marketmafioso/auth/login`, not root `/auth/login`.

Dashboard routes and JSON endpoints require an active dashboard session. Login accepts the existing dashboard user table and password hash model. The bootstrap user behavior can remain, but the browser experience should become a normal login screen.

Cookies should be:

- `HttpOnly`
- `Secure` when served over HTTPS
- `SameSite=Lax`
- sliding or bounded expiration, configurable

Plugin API key auth remains separate and must not create dashboard sessions.

## API Boundary

The rebuild should split current server-rendered behavior into API groups.

Acquisition:

```http
GET    /api/acquisition/requests
POST   /api/acquisition/requests
POST   /api/acquisition/requests/{id}/cancel
POST   /api/acquisition/requests/{id}/resend
GET    /api/acquisition/events/stream
```

Plugin acquisition contract uses the same API namespace as browser JSON endpoints, but with client API key auth instead of dashboard cookie auth:

```http
GET    /api/acquisition/requests/pending
POST   /api/acquisition/requests/{id}/claim
POST   /api/acquisition/requests/{id}/accept
POST   /api/acquisition/requests/{id}/reject
POST   /api/acquisition/requests/{id}/progress
POST   /api/acquisition/requests/{id}/complete
POST   /api/acquisition/requests/{id}/fail
```

Inventory:

```http
GET    /api/inventory/characters
GET    /api/inventory/latest
GET    /api/inventory/items
GET    /api/inventory/snapshots
GET    /api/inventory/snapshots/{id}
DELETE /api/inventory/snapshots/{id}
```

Shared XIV data:

```http
GET /api/xivdata/items/search
GET /api/xivdata/items/{id}
```

Diagnostics:

```http
GET /api/diagnostics/events
GET /api/diagnostics/events/stream
GET /api/diagnostics/events/{id}
```

Settings:

```http
GET /api/settings/dashboard
PUT /api/settings/dashboard
```

## SSE Model

Use SSE as the primary live-update channel for dashboard state.

Recommended stream:

```http
GET /api/events/stream?since=<eventId>
```

Named event types:

- `snapshot`: initial batch of current state.
- `diagnostic`: persistent diagnostic event.
- `acquisition-request`: request created, updated, cancelled, resent, completed, or failed.
- `acquisition-progress`: plugin route or purchase progress.
- `inventory`: inventory snapshot received or pruned.
- `heartbeat`: keepalive and reconnect health.
- `gap`: client requested an event id that has already fallen out of retention.

The Blazor client should show stream health somewhere unobtrusive:

- connected/disconnected,
- last event id,
- reconnect count,
- gap detected.

## Persistent Diagnostics

Add one persistent diagnostic event spine:

```sql
CREATE TABLE IF NOT EXISTS diagnostic_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at_utc TEXT NOT NULL,
    received_at_utc TEXT NOT NULL,
    source TEXT NOT NULL,
    category TEXT NOT NULL,
    type TEXT NOT NULL,
    severity TEXT NOT NULL,
    outcome TEXT NULL,
    message TEXT NOT NULL,
    correlation_id TEXT NULL,
    account_id INTEGER NULL,
    dashboard_user_id INTEGER NULL,
    dashboard_session_id TEXT NULL,
    plugin_instance_id TEXT NULL,
    acquisition_request_id TEXT NULL,
    route_run_id TEXT NULL,
    route_stop_id TEXT NULL,
    purchase_attempt_id TEXT NULL,
    snapshot_id TEXT NULL,
    item_id INTEGER NULL,
    item_name TEXT NULL,
    world TEXT NULL,
    character_name TEXT NULL,
    http_method TEXT NULL,
    route_pattern TEXT NULL,
    status_code INTEGER NULL,
    duration_ms INTEGER NULL,
    exception_type TEXT NULL,
    exception_message TEXT NULL,
    payload_summary_json TEXT NULL,
    payload_raw_json TEXT NULL,
    payload_size_bytes INTEGER NULL,
    payload_sha256 TEXT NULL
);
```

Indexes:

- `occurred_at_utc DESC`
- `category, occurred_at_utc DESC`
- `severity, occurred_at_utc DESC`
- `correlation_id`
- `acquisition_request_id`
- `snapshot_id`

Retention:

- default: 5000 events,
- configurable high-water mark,
- pruning should be separate from snapshot retention,
- pruning should emit a diagnostic event summarizing what was removed.

## Diagnostic Taxonomy

Categories:

- `server.http`
- `server.storage`
- `server.sse`
- `server.config`
- `dashboard.client`
- `dashboard.preferences`
- `auth.session`
- `auth.api-key`
- `plugin.pickup`
- `plugin.lifecycle`
- `acquisition.request`
- `acquisition.route`
- `acquisition.purchase`
- `inventory.ingest`
- `inventory.retention`
- `vps.deploy`
- `vps.smoke`

Severities:

- `Trace`
- `Debug`
- `Info`
- `Warn`
- `Error`
- `Critical`

Outcomes:

- `Started`
- `Succeeded`
- `Failed`
- `Rejected`
- `Skipped`
- `TimedOut`
- `Cancelled`
- `Retried`
- `Replayed`

## Correlation IDs

Correlation starts in the dashboard client when a user creates an acquisition request.

That correlation id should then flow through:

1. dashboard request builder,
2. server acquisition request row,
3. pending request payload,
4. plugin claim,
5. plugin accept/reject,
6. route run,
7. route stop,
8. purchase attempts,
9. progress/complete/fail posts,
10. diagnostics view.

The current acquisition request id remains important, but it should not be the only diagnostic spine. A single dashboard action may cause multiple server events before a request id exists.

## Payload Storage And Redaction

Diagnostics should store enough to reconstruct what happened. The default policy is:

- store sanitized raw event payloads up to a configurable inline cap,
- store summaries and hashes for larger payloads,
- never duplicate full inventory raw JSON inside diagnostics,
- link inventory events to snapshot ids and raw JSON report ids instead.

Always redact or hash:

- `Authorization`,
- `Cookie`,
- `Set-Cookie`,
- `X-Api-Key`,
- Basic Auth credentials,
- dashboard passwords,
- API keys,
- claim tokens,
- session secrets,
- raw plugin configuration,
- raw game memory dumps.

Idempotency keys may be hashed when useful for replay diagnosis.

## Settings Diagnostics UI

Diagnostics live under Settings and should support:

- live event stream,
- filters by category, severity, source, correlation id, request id, snapshot id, item, world,
- event detail drawer,
- raw sanitized payload viewer,
- copy correlation id,
- copy event JSON,
- SSE health indicator,
- retention status,
- gap warnings.

The main Acquisition page should show only compact operational status and links into filtered diagnostics. It should not become a log viewer.

## Settings Snapshots UI

Snapshots live under Settings and should support:

- latest snapshot summary,
- snapshot list,
- raw JSON links,
- delete one snapshot,
- delete/prune snapshots according to retention policy,
- structured inventory store health.

The Inventory page should stay focused on browsing current inventory. Snapshot management belongs in Settings.

## Server Diagnostics Sources

The server should record events for:

- dashboard login success/failure/logout/session expiry,
- plugin API key accepted/rejected by surface,
- acquisition request create/cancel/resend,
- plugin pending/claim/accept/reject/progress/complete/fail,
- lifecycle transition conflicts,
- inventory ingest accepted/rejected,
- snapshot import/prune/delete,
- SQLite migration and write failures,
- item lookup failures,
- SSE connect/disconnect/reconnect/gap.

## Plugin Diagnostics Sources

The plugin should continue writing local route logs for deep UI automation investigation, but it should also report high-value summary events to the server during active acquisition runs:

- claim restored,
- request accepted,
- route started,
- stop travel command sent,
- world arrived,
- market board opened,
- item searched,
- live listings read,
- purchase attempt started,
- purchase accepted,
- purchase skipped with reason,
- world complete,
- route complete/under-procured/stopped/failed.

The server event record should contain local diagnostic file path when useful, but the server should not require direct filesystem access to the plugin logs.

## Failure Shapes To Make Visible

Diagnostics should explicitly classify:

- API key rejection by route family,
- cookie/session expiry,
- dashboard validation failure,
- item lookup failure,
- idempotency replay/conflict,
- claim token mismatch without storing the token,
- pending or claim expiry,
- plugin pickup empty vs unauthorized,
- server lifecycle transition conflict,
- SSE disconnect/reconnect/gap,
- inventory invalid JSON,
- empty inventory report,
- raw JSON pruned,
- DB write failure,
- route UI blocked,
- current world unavailable or wrong,
- item search timeout,
- listing missing,
- price/HQ/budget skip,
- confirmation timeout,
- ambiguous UI,
- unknown purchase result,
- VPS health/auth/ingest smoke failures.

For VPS smoke and deploy diagnostics, root-level Caddy validation is not enough. The service-user `caddy adapt` check should be treated as a first-class smoke result.

## Hosting And Route Shape

The current hosted dev receiver lives under `/api/marketmafioso` because it began as an API-shaped receiver mounted beside Craft Architect. The dashboard rebuild intentionally breaks from that shape instead of preserving it as a compatibility surface.

Canonical hosted shape:

- `/marketmafioso/` serves the browser dashboard.
- `/marketmafioso/settings`, `/marketmafioso/inventory`, `/marketmafioso/overview`, and other non-API child routes are dashboard routes.
- `/marketmafioso/api/*` is the canonical machine/API namespace for dashboard JSON, plugin ingest, plugin acquisition pickup/lifecycle, inventory reads, XIV data, diagnostics, and SSE.
- `/api/marketmafioso/*` is retired during the migration. Existing plugin configs, derived dashboard links, docs, and deployed smoke scripts must move to the canonical shape in the same pass.

Migration pass:

- Set the hosted dashboard base path to `/marketmafioso`.
- Keep the Blazor shell base-path-safe by rewriting `<base href>` from the active `PathBase` and by using relative dashboard navigation links.
- Add or promote canonical API routes under `/api/*` inside the MarketMafioso app so the public hosted paths become `/marketmafioso/api/*`.
- Remove the `/api/marketmafioso/*` hosted route from Caddy/deploy configuration once plugin defaults and smoke checks point at `/marketmafioso/api/*`.
- Make route diagnostics report requests that hit an unrecognized or retired route shape as configuration errors, not compatibility traffic.

Long-term:

- Treat `/marketmafioso/api/*` as machine/API/plugin space.
- If a dedicated subdomain is introduced later, serve the dashboard at `https://marketmafioso.dev.xivcraftarchitect.com/` and machine endpoints at `https://marketmafioso.dev.xivcraftarchitect.com/api/*`.
- For self-hosted single-purpose MarketMafioso receivers, serving the dashboard at `/` is acceptable and probably the simplest default.
- Do not move Craft Architect away from `/` casually. It is the historical primary app for the domain, and moving it to `/craftarchitect/` would require a separate migration for Blazor base href, browser storage, bookmarks, app links, service-worker/static asset behavior if present, and docs.

The practical migration is therefore to leave Craft Architect at the root for now and move MarketMafioso to `/marketmafioso/` with `/marketmafioso/api/*` during the dashboard rebuild.

## Compatibility And Migration

Keep these working while replacing the browser UI:

- plugin inventory POSTs,
- plugin dashboard URL derivation,
- plugin acquisition pending/claim/progress endpoints,
- raw report JSON access,
- structured inventory database.

The HTML dashboard routes can become Blazor static host routes. JSON and plugin routes move behind `/marketmafioso/api/*` for hosted deployments. Plugin routes should not be moved casually after this migration is complete.

## Implementation Priorities

### Current Status

- Done: Dashboard client project and static hosting exist.
- Done: Server-side dashboard shell hosting rewrites Blazor `<base href>` from the configured receiver base path.
- Done: Cookie-backed dashboard login/session APIs exist.
- Done: Acquisition is the first Blazor page and uses typed client services for item lookup, queue staging, SSE queue updates, cancel, and resend.
- Partial: Persistent diagnostics storage exists and diagnostics can be viewed from Settings, but the Settings IA is still mostly a shell.
- Partial: Acquisition UI is functional but still needs deployment of the latest base-path-safe navigation/settings polish, plus continued spacing and table polish.
- Pending: Inventory, Overview, Snapshots, and the full Diagnostics workspace have not been rebuilt to the same standard as Acquisition.
- Pending: Move the hosted receiver to the canonical `/marketmafioso/` dashboard and `/marketmafioso/api/*` machine route shape, retiring `/api/marketmafioso/*` in the same pass.

### Next Priorities

1. Migrate the dev-hosted route shape to `/marketmafioso/` and `/marketmafioso/api/*`, then remove `/api/marketmafioso/*` from the hosted route surface.
2. Stabilize the dashboard shell: base-path-safe navigation, one refresh model, and readable control spacing.
3. Make Settings real enough for Acquisition: default character, default routing, default expiry, and account-scoped character choices.
4. Replace free-typed Acquisition target character/world fields with a character selector populated from account-associated inventory characters.
5. Rebuild Inventory against structured JSON APIs.
6. Move Diagnostics and Snapshots into full Settings workspaces with filters, detail drawers, and retention visibility.
7. Remove server-rendered dashboard HTML once equivalent Blazor pages exist.

## Non-Goals For This Pass

- Public multi-user hosting.
- Cross-account hosted tenancy.
- Remote-control of active purchases from the dashboard.
- Replacing plugin-side consent and local execution authority.
- Moving MarketMafioso into the Craft Architect repository.
- Rebuilding plugin ImGui UI.
