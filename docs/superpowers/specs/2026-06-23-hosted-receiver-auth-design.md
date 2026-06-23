# Hosted Receiver Auth Design

## Goal

Make the hosted MarketMafioso receiver safe enough to leave online while keeping it simple enough that auth does not become the project. The receiver needs three different access shapes:

- Plugin clients need machine-to-machine access for sending snapshots.
- Automation or manual API clients may need separate machine-to-machine read access for report JSON.
- Browser users need a protected dashboard and report viewer for inspecting stored snapshots.

The first implementation should support the dev VPS receiver. Production should use the same shape later, with separate credentials and storage.

## Current State

The dev receiver is deployed at:

```text
https://dev.xivcraftarchitect.com/api/marketmafioso/
```

Current live routes:

- `GET /health` is public.
- `POST /inventory` and `POST /api/inventory` require `X-Api-Key` when hosted auth is enabled.
- `GET /api/reports...` requires `X-Api-Key` when hosted auth is enabled.
- HTML dashboard routes exist in the ASP.NET app but are not intentionally exposed through Caddy yet.

The plugin already persists `Configuration.ApiKey` and sends it as `X-Api-Key`. In the auth pass, this becomes an ingest key. The plugin does not need report-read or delete privileges.

## Non-Goals

- No user account database.
- No OAuth.
- No JWT/session-cookie identity system.
- No multi-tenant permissions model.
- No production receiver deployment as part of the dev auth pass.
- No destructive API surface for the plugin key.

These can be revisited only if the receiver becomes shared beyond a small trusted operator group.

## Recommended Architecture

Use two auth layers, each suited to its caller:

1. API key auth in the ASP.NET receiver for plugin ingest and optional report-read API traffic.
2. Caddy Basic Auth for browser dashboard traffic.

This keeps the app responsible for machine API decisions and keeps browser login complexity outside the app for now.

## Route Policy

Public routes:

```text
GET /api/marketmafioso/health
```

Ingest API-key routes:

```text
POST   /api/marketmafioso/inventory
POST   /api/marketmafioso/api/inventory
```

Read API-key routes:

```text
GET    /api/marketmafioso/api/reports
GET    /api/marketmafioso/api/reports/latest
GET    /api/marketmafioso/api/reports/{id}
GET    /api/marketmafioso/api/reports/{id}/view
```

Read API routes are optional for the first dev receiver pass. If no read key is configured, hosted `/api/reports...` routes should fail closed rather than falling back to the ingest key or public access. The browser dashboard remains the normal report inspection path.

Basic-Auth browser routes:

```text
GET  /api/marketmafioso
GET  /api/marketmafioso/
GET  /api/marketmafioso/reports/{id}
GET  /api/marketmafioso/reports/{id}/json
GET  /api/marketmafioso/reports/latest/json
POST /api/marketmafioso/reports/{id}/delete
POST /api/marketmafioso/reports/delete-all
```

The dashboard should not depend on API-key headers from the browser. Browser links and forms should stay on browser routes. Raw JSON links shown in the dashboard should point to dashboard-safe export routes rather than the API-key-only `/api/reports...` endpoints.

The plugin ingest key can create snapshots but cannot read or delete them. Hosted delete operations are browser-admin actions only. If remote automation needs deletion later, add a separate admin key that is never stored by the plugin.

## Dashboard Export Routes

Add browser-facing JSON export routes protected by Caddy Basic Auth:

```text
GET /api/marketmafioso/reports/{id}/json
GET /api/marketmafioso/reports/latest/json
```

These return the same JSON payloads as the API routes but are reachable from the dashboard without requiring a browser extension or manual `X-Api-Key` header. They are deliberately separate from `/api/reports...` so API traffic remains API-key-only.

The `Location` header returned from hosted `201 Created` responses should include the configured `PathBase`, for example `/api/marketmafioso/api/reports/{id}`. This makes plugin status messages and manual debugging point at real hosted routes.

## Credential Model

Dev environment credentials:

- `MARKETMAFIOSO_DEV_INGEST_API_KEY`: plugin ingest key stored as a GitHub Secret and installed into the systemd environment file during deploy.
- `MARKETMAFIOSO_DEV_PREVIOUS_INGEST_API_KEY`: optional previous plugin ingest key used only during planned rotation.
- `MARKETMAFIOSO_DEV_READ_API_KEY`: optional report-read API key stored as a GitHub Secret and installed into the systemd environment file during deploy.
- `MARKETMAFIOSO_DEV_PREVIOUS_READ_API_KEY`: optional previous report-read API key used only during planned rotation.
- `MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD`: dashboard password stored as a GitHub Secret.

Local operator copy:

- The dev dashboard username is the fixed non-secret value `marketmafioso`.
- The dev ingest API key can continue to live at `C:\Users\gianf\.ssh\marketmafioso_dev_api_key.txt` for manual plugin setup.
- The read API key can live in a separate local helper file only if manual API calls need it.
- The dashboard password should not be committed. If a local helper file is used, it should live outside the repo with the API key.
- Dashboard passwords should be long random strings. The workflow should generate the Caddy hash during deploy without printing it, then install only the hash on the VPS. GitHub Actions keeps the plaintext password secret so it can perform authenticated smoke tests.
- API keys should be high-entropy random values. Use at least 128 bits of entropy; 32 random bytes encoded as base64url or hex is preferred. Ingest, read, previous, dev, and future production credentials must all be distinct values.

Production should use separate secret names later:

```text
MARKETMAFIOSO_PROD_INGEST_API_KEY
MARKETMAFIOSO_PROD_PREVIOUS_INGEST_API_KEY
MARKETMAFIOSO_PROD_READ_API_KEY
MARKETMAFIOSO_PROD_PREVIOUS_READ_API_KEY
MARKETMAFIOSO_PROD_BASIC_AUTH_PASSWORD
```

## Caddy Shape

The dev Caddy site should import a MarketMafioso fragment before the generic Craft Architect SPA fallback. The workflow must verify or repair this ordering on every deploy, not merely skip insertion when an import string already exists.

The fragment should route exact API and dashboard paths in this order:

1. Public health.
2. API-key app routes without Caddy Basic Auth.
3. Browser dashboard routes with Caddy Basic Auth.
4. Fall through to the existing Craft Architect app only for unrelated paths.

The fragment should preserve `/api/marketmafioso` when proxying. The ASP.NET app continues to use `MarketMafioso__BasePath=/api/marketmafioso`.

The Caddy fragment should protect both `/api/marketmafioso` and `/api/marketmafioso/`. The no-slash route may either return Basic Auth `401` directly or redirect to the protected slash route, but it must not fall through to the Craft Architect SPA.

The Caddy fragment should enable access logging for MarketMafioso routes. Brute-force lockout tooling can stay outside the first implementation, but the password must be long and repeated `401` responses on both Basic Auth routes and API-key routes must be visible in logs. Never log `X-Api-Key` header values.

## Plugin UX

Keep the existing editable endpoint and API key fields. Add targeted guardrails:

- Classify the current `ServerUrl`, not the last preset button clicked. Hosted URLs require an API key even if the user typed or pasted the URL manually.
- Endpoint classification:
  - Local: `localhost`, `127.0.0.1`, and `[::1]`.
  - Known hosted: `dev.xivcraftarchitect.com` and, once deployed, `xivcraftarchitect.com`.
  - Custom remote: any other `http` or `https` URL.
  - Invalid: missing or unsupported URL.
- Known hosted and custom remote endpoints require an API key by default. Custom remote can show a softer warning, but it should still avoid silently sending unauthenticated inventory to the public internet.
- When the current URL is hosted, show a warning if the API key field is empty and label the field as required.
- Before sending a report to a hosted URL, fail locally with a clear message if the API key is empty.
- Keep local receiver behavior unchanged; `http://localhost:8080/inventory` must not require a key by default.
- Keep the API key masked by default with an explicit show/hide toggle.
- Disable or clearly label the `Production VPS` preset as unavailable until the production receiver exists. Dev should be the only hosted preset that looks ready in this pass.
- After a hosted `201 Created`, parse the response summary and show the stored snapshot id plus the direct dashboard report URL, for example `https://dev.xivcraftarchitect.com/api/marketmafioso/reports/{id}`.
- Map hosted `401` responses to a targeted message: the receiver rejected the plugin API key, so check the saved API key for this endpoint.

Do not auto-fill secrets from disk inside the plugin. Reading `C:\Users\gianf\.ssh\...` from a Dalamud plugin would be too machine-specific and would blur the boundary between local operator convenience and distributable plugin behavior.

## Server Behavior

API-key behavior should stay explicit:

- If `MarketMafioso:RequireApiKey=true` and no current ingest key is configured, startup fails.
- The server should accept one current ingest key and one optional previous ingest key during planned rotation.
- The server should accept one optional current read key and one optional previous read key for report-read API routes. Read keys do not authorize ingestion or deletion.
- If no current read key is configured in hosted mode, report-read API routes are disabled and fail closed. Do not allow the ingest key to read reports.
- A previous read key is invalid unless a current read key is also configured.
- Missing or wrong `X-Api-Key` returns `401`.
- API `401` responses should return small non-secret JSON, such as `{ "error": "invalid_api_key" }`.
- API key validation should be centralized, reject empty or repeated header values, and use fixed-time comparison.
- Public health remains unauthenticated.
- Dashboard HTML routes do not check API keys in the app; Caddy owns browser auth.
- Hosted API `DELETE /api/reports...` routes should be removed or disabled. The plugin key must not authorize destructive operations.
- Hosted dashboard pages should hide full filesystem paths. Show an environment label such as `dev receiver storage`; keep exact paths in operator docs and logs.

The app should not infer authorization from Caddy headers in this pass. If the app is ever exposed without Caddy Basic Auth, dashboard routes would be public, so deployment validation must verify the Caddy route.

Dashboard POST delete forms need CSRF protection before they are publicly reachable through Caddy Basic Auth. Use server-generated form nonces and validate `Origin` or `Referer` against the configured hosted origin. Tests should cover missing/invalid nonce rejection and valid nonce success.

CSRF origin validation should use an explicit setting:

```text
MarketMafioso__IngestApiKey=<current-ingest-key>
MarketMafioso__PreviousIngestApiKey=<optional-previous-ingest-key>
MarketMafioso__ReadApiKey=<optional-current-read-key>
MarketMafioso__PreviousReadApiKey=<optional-previous-read-key>
MarketMafioso__PublicOrigin=https://dev.xivcraftarchitect.com
```

The workflow should install these settings in the systemd environment file. Local development may leave `PublicOrigin` blank or set it to the local server origin.

## Workflow Behavior

The dev deploy workflow should:

1. Require all VPS SSH secrets.
2. Require `MARKETMAFIOSO_DEV_INGEST_API_KEY`.
3. Accept optional `MARKETMAFIOSO_DEV_PREVIOUS_INGEST_API_KEY`.
4. Accept optional `MARKETMAFIOSO_DEV_READ_API_KEY`.
5. Accept optional `MARKETMAFIOSO_DEV_PREVIOUS_READ_API_KEY`.
6. Require `MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD`.
7. Use the fixed Basic Auth username `marketmafioso`.
8. Generate the Caddy Basic Auth hash on the GitHub Actions runner without printing it.
9. Keep the plaintext Basic Auth password on the runner only; never upload it to the VPS.
10. Write local secret files with `600` permissions.
11. Upload secret files into a remote `mktemp -d` directory with `700` permissions.
12. Install a remote `trap` so temporary secret files are deleted on both success and failure.
13. Install/update the systemd environment file, including `MarketMafioso__PublicOrigin=https://dev.xivcraftarchitect.com`.
14. Install/update the Caddy fragment.
15. Verify or repair Caddy import ordering before the SPA fallback.
16. Validate Caddy config.
17. Restart the receiver service.
18. Reload Caddy.
19. Verify public health.
20. Verify unauthenticated dashboard slash route returns `401`.
21. Verify unauthenticated dashboard no-slash route returns `401` or redirects to the protected slash route.
22. Verify authenticated dashboard access returns HTML from the MarketMafioso dashboard.
23. Verify unauthenticated inventory POST returns `401`.
24. Verify authenticated inventory POST returns `201`.
25. If a read key is configured, verify the read API accepts the read key and rejects the ingest key.
26. If no read key is configured, verify hosted read API routes fail closed.

The workflow should not print secrets or derived auth headers.

Expected `401` checks should capture status codes rather than using `curl --fail`. Authenticated dashboard smoke tests should use `curl --user "marketmafioso:$MARKETMAFIOSO_DEV_BASIC_AUTH_PASSWORD"` and should assert that the response is the MarketMafioso dashboard, not the Craft Architect SPA fallback.

## Testing Strategy

Server tests:

- Hosted mode rejects inventory without `X-Api-Key`.
- Hosted mode accepts inventory with the configured ingest key.
- Hosted mode accepts inventory with the configured previous ingest key when present.
- Hosted mode rejects inventory with the read key.
- Hosted mode keeps `/health` public.
- Hosted mode protects `/api/reports...`.
- Hosted mode accepts `/api/reports...` with the read key when configured.
- Hosted mode rejects `/api/reports...` with the ingest key.
- Hosted mode fails closed for `/api/reports...` when no read key is configured.
- Hosted mode does not expose API-key delete routes for plugin/API clients.
- Dashboard export routes return JSON for known reports.
- Dashboard links use browser routes, not API-key-only routes.
- Hosted base path routes produce `/api/marketmafioso/...` links.
- Hosted `201 Created` location includes `/api/marketmafioso`.
- Dashboard POST delete routes reject missing or invalid CSRF data and accept valid CSRF data.
- CSRF tests cover valid origin, invalid origin, missing nonce, and valid nonce success under `PathBase`.
- Hosted dashboard tests assert the configured environment label appears and the temp content root/report directory path does not appear.

Workflow/live smoke:

- GitHub Actions deploy succeeds.
- `marketmafioso-dev` is active.
- Caddy validates and reloads.
- Public health returns `200`.
- Dashboard route returns `401` without Basic Auth.
- Dashboard no-slash route returns `401` or redirects to the protected slash route.
- Dashboard route returns HTML with Basic Auth.
- Inventory ingest returns `401` without API key.
- Inventory ingest returns `201` with API key.
- When configured, report-read API accepts the read key and rejects the ingest key.
- When no read key is configured, report-read API fails closed.

Plugin checks:

- Hosted URL plus empty API key fails before HTTP send with a clear chat/status message.
- Hosted URL plus API key still sends `X-Api-Key`.
- Local URL with empty API key remains allowed.
- Hosted URL classification is based on the current URL text, including manually edited URLs.
- Hosted `401` produces a targeted API key message.
- Hosted `201` records and displays the stored snapshot id and direct dashboard report URL.

## Implementation Slices

1. Add dashboard-safe JSON export routes and update dashboard links.
2. Remove or disable hosted API delete routes for plugin/API clients.
3. Add CSRF protection for dashboard delete forms.
4. Split ingest and optional read API keys, then centralize API key validation, fixed-time comparison, previous-key rotation support, and non-secret `401` JSON.
5. Add plugin hosted-endpoint classification, API-key guardrails, targeted `401` messaging, and stored snapshot id display.
6. Update dev workflow to require Basic Auth password, generate the Caddy hash on the runner, use safer temp secret handling, repair Caddy import ordering, and install the dashboard route.
7. Add live smoke verification for Basic Auth and ingest auth.
8. Update `docs/hosted-receiver.md` with first-time setup, operator credential generation, dashboard access, production-unavailable wording, and credential rotation notes.

## Open Operational Notes

Credential rotation should avoid surprise downtime:

1. Generate a new ingest key.
2. Move the current ingest key into `MARKETMAFIOSO_DEV_PREVIOUS_INGEST_API_KEY`.
3. Put the new ingest key in `MARKETMAFIOSO_DEV_INGEST_API_KEY`.
4. Rerun the deploy workflow.
5. Update the plugin or password manager with the new credential.
6. After the plugin is updated and sends successfully, clear `MARKETMAFIOSO_DEV_PREVIOUS_INGEST_API_KEY` and rerun deploy.

Read key setup is optional. If enabled, rotation follows the same current/previous pattern using `MARKETMAFIOSO_DEV_READ_API_KEY` and `MARKETMAFIOSO_DEV_PREVIOUS_READ_API_KEY`.

Basic Auth password rotation remains manual:

1. Generate a new long random password.
2. Update the GitHub password secret.
3. Rerun the deploy workflow.
4. Update the password manager with the new dashboard credential.

This is acceptable for the current single-operator dev receiver. If rotation becomes frequent, add a small helper script later.

First-time operator setup should be documented with copy-pasteable PowerShell:

1. Generate the dev ingest API key and dashboard password.
2. Optionally generate a separate read API key for manual API access.
3. Store local copies outside the repo.
4. Set GitHub Secrets.
5. Run the deploy workflow.
6. Paste only the ingest API key into the plugin.
7. Open the Basic-Auth dashboard.
