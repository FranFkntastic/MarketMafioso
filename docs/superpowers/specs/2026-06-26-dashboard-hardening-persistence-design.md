# Dashboard Hardening And Persistence Design

## Goal

Harden the MarketMafioso receiver dashboard so acquisition pages survive reloads and tab switches, remove dashboard CSRF overhead, and provide a server/browser hybrid options menu for durable user preferences such as the default character.

## Scope

This slice covers the receiver dashboard under `MarketMafioso.Server`, with the first implementation focused on `/acquisition`. It includes the persistence substrate needed for dashboard preferences, but it does not introduce public multi-user hosting, plugin-side configuration, or broader inventory browser redesign.

The dashboard remains self-hosted/private-first. Hosted receiver dashboard routes continue to use Basic Auth when configured, and plugin/API acquisition routes continue to use the existing client API key and claim-token contracts.

## Current Problems

- The acquisition dashboard shell uses fixed grid rows (`54px 1fr 30px`) while mobile header/footer content can wrap, creating overlap risk.
- Browser dashboard form posts carry CSRF token overhead that creates stale-token failures and makes a private self-hosted receiver harder to operate.
- The acquisition page has useful transient state, but filters, staged queue rows, form entries, and selected/default character are lost across reloads.
- There is no dashboard options surface for setting a default character or future user-level preferences.
- Browser-only state is not enough for authenticated hosted dashboards; server save state should follow the dashboard user when possible.

## Persistence Model

Dashboard preferences use a hybrid model:

1. Server preference state is authoritative when it exists.
2. Browser `localStorage` is the fallback when the server has no saved state or a server save fails.
3. Server-rendered defaults are the final fallback.
4. Explicit option saves write to the server first and mirror the accepted value to browser storage.
5. Page state updates write to browser storage immediately and may be promoted to server preferences only for fields considered durable preferences.

Server preferences are stored in SQLite as scoped JSON, keyed by dashboard owner:

```sql
CREATE TABLE IF NOT EXISTS dashboard_preferences (
    owner_kind TEXT NOT NULL,
    owner_key TEXT NOT NULL,
    scope TEXT NOT NULL,
    preferences_json TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (owner_kind, owner_key, scope)
);
```

The owner key is:

- `owner_kind = "dashboard-user"` and `owner_key = "<dashboard_users.id>"` when Basic Auth identifies a dashboard user.
- `owner_kind = "receiver-local"` and `owner_key = "default"` when dashboard auth is disabled.

This avoids nullable uniqueness problems and keeps local/no-auth receiver behavior deterministic.

## Preference Shape

The acquisition dashboard server preference JSON is versioned:

```json
{
  "schemaVersion": 1,
  "defaultCharacterId": 123,
  "autoRefreshEnabled": true,
  "restoreQueueFilters": true,
  "updatedAtUtc": "2026-06-26T00:00:00.0000000+00:00"
}
```

Browser page state is also versioned, but it contains additional transient fields:

```json
{
  "schemaVersion": 1,
  "serverPreferenceUpdatedAtUtc": "2026-06-26T00:00:00.0000000+00:00",
  "defaultCharacterId": 123,
  "autoRefreshEnabled": true,
  "restoreQueueFilters": true,
  "queueFilterText": "shard",
  "queueStatusFilter": "Pending pickup",
  "requestForm": {
    "targetCharacterName": "Wei Ning",
    "targetWorld": "Gilgamesh",
    "region": "North America",
    "quantityMode": "Exact",
    "quantity": "10",
    "hqPolicy": "Either",
    "maxUnitPrice": "99",
    "maxTotalGil": "",
    "worldMode": "Recommended",
    "expiresInSeconds": "300"
  },
  "selectedItem": {
    "itemId": 2,
    "itemName": "Fire Shard",
    "itemType": "Shard"
  },
  "stagedQueueRows": []
}
```

The browser key is:

```text
marketmafioso.dashboard.acquisition.v1
```

The browser state mirrors durable server preferences so fallback and tab synchronization stay simple, while transient form/queue data stays browser-local.

## Server API

Add dashboard-only endpoints:

```http
GET /dashboard/preferences/acquisition
PUT /dashboard/preferences/acquisition
```

`GET` returns:

- `200 OK` with a preference object when server state exists.
- `404 Not Found` with `{ "error": "preferences_not_found" }` when no server state exists.

`PUT` accepts a JSON body and returns the normalized saved preference object. The endpoint must be covered by dashboard auth when auth is enabled. It must not accept the plugin client API key as dashboard authorization.

The server normalizes preference payloads:

- `schemaVersion` must equal `1`.
- `defaultCharacterId` may be null or one of the receiver's known character IDs for account `1`.
- `autoRefreshEnabled` defaults to true.
- `restoreQueueFilters` defaults to true.
- unknown fields are ignored rather than persisted.

Invalid preference payloads return JSON with `400 Bad Request` and a clear error code, because these endpoints are called by dashboard JavaScript.

## Dashboard User Context

`DashboardBasicAuthMiddleware` should set the current dashboard user ID in `HttpContext.Items` after successful authentication. A small helper resolves the preference owner from request context:

- if a dashboard user ID exists: `dashboard-user/<id>`
- otherwise, if dashboard auth is disabled: `receiver-local/default`
- otherwise: no owner, causing preference endpoints to challenge or fail through existing auth behavior

The helper keeps route code from re-parsing Basic Auth headers.

## Options Menu

The acquisition dashboard gets an Options button in the queue/request header area. It opens a compact modal or panel that contains:

- Default character select.
- Auto-refresh enabled checkbox.
- Restore queue filters checkbox.
- Save button.
- Clear local page state button.
- Reset server options button.

The initial UI can be plain HTML/CSS and inline JavaScript, matching the current server-rendered dashboard style. It should avoid decorative redesign.

## Page State Reload And Tab Behavior

On load:

1. The page parses server-rendered bootstrap JSON that includes known characters, current selected character, endpoint URLs, and default server-rendered settings.
2. The page requests server preferences.
3. If server preferences exist, they hydrate the durable option fields and mirror to localStorage.
4. If server preferences do not exist, localStorage hydrates the page.
5. If neither exists, server-rendered defaults remain active.

During use:

- Queue filter text and status filter persist to localStorage.
- Form fields persist to localStorage after input/change.
- Selected item persists with the form state.
- Client-side staged queue rows persist to localStorage.
- Default character is applied by setting the character select and request form target fields when the selected character is known.
- `storage` events hydrate other open dashboard tabs when the local key changes.

The page does not issue, persist, rotate, or inject dashboard CSRF tokens.

## Dashboard Mutation Model

Dashboard browser mutations do not use CSRF tokens. This receiver is private/self-hosted-first, and the extra stale-token failure mode is not worth the operational cost for the current deployment model.

The remaining boundaries are:

- Dashboard routes use Basic Auth or trusted external dashboard auth when configured.
- Plugin/API acquisition routes continue using the client API key and claim-token contracts.
- Dashboard preference endpoints stay dashboard-authenticated and must not accept plugin client API keys as authorization.

## Responsive Hardening

The acquisition dashboard shell should use:

```css
.shell { min-height: 100vh; display: grid; grid-template-rows: auto 1fr auto; }
```

The queue pane remains table-first, but mobile CSS should guarantee no header/footer overlap. The table wrapper may keep horizontal scrolling in this slice. A future visual pass can convert queue rows into compact cards if the table remains too dense after this hardening.

## Tests

Server tests should cover:

- `dashboard_preferences` table creation.
- Saving and loading preferences for a dashboard user.
- Saving and loading preferences for receiver-local/no-auth mode.
- Server preferences returning 404 when absent.
- Invalid preference payload rejection.
- Acquisition page includes bootstrap data, options UI, and state manager hooks.
- Dashboard mutation routes accept authorized browser posts without CSRF fields.
- Dashboard pages and JSON refresh payloads do not render or return CSRF tokens.
- Acquisition CSS uses `auto 1fr auto` shell rows.

Browser smoke should cover:

- default character save, reload, and second tab hydration.
- queue filter and status persistence across reload.
- form and staged queue persistence across reload.
- reload/tab persistence after staged queue submissions.

## Risks And Guardrails

- Do not store API keys, Basic Auth credentials, or claim tokens in localStorage.
- Do not reintroduce dashboard CSRF tokens without an explicit product decision.
- Do not let plugin client API keys authorize dashboard preference endpoints.
- Keep server state scoped to receiver-local or dashboard user identity; do not create a public/shared hosted preference model.
- Keep staged acquisition queue rows browser-local unless a future requirement explicitly asks for cross-device staged queues.
- Verify `dashboard-hardening` matches `local-dev` before every implementation change.
