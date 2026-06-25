# Shared XIV Data Gateway

## Goal

Add a small shared HTTP boundary for FFXIV item identity and metadata so MarketMafioso, Craft Architect, and future hosted tools do not each implement their own Garland/XIVAPI search behavior.

The first implementation may live inside the Craft Architect hosted backend to avoid new deployment machinery, but it should be designed as an extractable gateway. The route group, DTOs, provider interfaces, tests, cache, and configuration should not depend on Craft Architect UI state or MarketMafioso receiver state.

## First Consumer

MarketMafioso Market Acquisition needs a dashboard item selector that works like Craft Architect's Plan Builder:

- The user searches by item name.
- The UI shows selectable item results with metadata.
- Selecting a result resolves the hidden `itemId`.
- Numeric entry can resolve to the same selected-item model.
- Unresolved free text cannot stage a request.
- A queue builder can add one or more resolved items before staging acquisition requests.

The MarketMafioso plugin pickup contract remains one acquisition request per item for the first pass. The dashboard can stage multiple rows by creating one normal request per queue row.

## Route Shape

Initial hosted routes should live under the Craft Architect API base path:

```text
GET /api/xivdata/items/search?q=darksteel&limit=20
GET /api/xivdata/items/{itemId}
```

If deployed behind the existing Craft Architect host, `https://xivcraftarchitect.com/api/xivdata/...` and `https://dev.xivcraftarchitect.com/api/xivdata/...` should be the public shapes.

MarketMafioso should consume the gateway through configuration:

```text
MarketMafioso:XivDataBaseUrl=https://dev.xivcraftarchitect.com/api/xivdata
```

Self-hosted MarketMafioso packages should not silently depend on our hosted gateway. Docs must tell self-hosters to configure an XIV data base URL or accept that dashboard item search is unavailable. Missing gateway configuration should produce explicit UI and log diagnostics, not a hidden fallback.

## DTO Contract

Return source-neutral DTOs owned by us. Do not return Garland-shaped payloads.

```json
{
  "itemId": 5057,
  "name": "Darksteel Nugget",
  "iconId": 21203,
  "itemType": "Metal",
  "canBeHq": true,
  "isMarketable": true,
  "stackSize": 999,
  "source": "garland",
  "sourceUpdatedAtUtc": "2026-06-25T20:00:00Z"
}
```

Search response:

```json
{
  "query": "darksteel",
  "items": [
    {
      "itemId": 5057,
      "name": "Darksteel Nugget",
      "iconId": 21203,
      "itemType": "Metal",
      "canBeHq": true,
      "isMarketable": true,
      "stackSize": 999,
      "source": "garland",
      "sourceUpdatedAtUtc": "2026-06-25T20:00:00Z"
    }
  ]
}
```

Fields may be nullable only when the upstream source truly lacks the value. `itemId`, `name`, and `source` are required.

## Provider Boundary

The gateway should hide current provider details behind an interface such as:

```csharp
public interface IXivItemDataProvider
{
    Task<IReadOnlyList<XivItemSummary>> SearchItemsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<XivItemSummary?> GetItemAsync(
        uint itemId,
        CancellationToken cancellationToken);
}
```

First provider:

- Garland search for name lookup.
- Garland item detail for metadata when search results do not provide enough.

Later providers:

- XIVAPI-backed sheet projection.
- Lumina-generated static exports.
- Teamcraft CDN bridges for edge cases.

The gateway should not expose which provider supplied a field except for diagnostic `source` metadata.

## Cache

Use a server-side SQLite or file-backed cache attached to the hosting service.

Rules:

- Cache positive item lookups by `itemId`.
- Cache search result summaries by normalized query for a short TTL.
- Keep exact-item cache longer than search cache.
- Failed provider responses should not poison the cache.
- Cache records should store the source name and fetch timestamp.
- A stale cached item may be served only when the provider is unavailable and the cached row has required fields.

The cache should be local to the deployment. It is not a user data store.

## Auth And Rate Limits

The first route can be publicly readable because it returns game metadata only, but it must be rate-limited.

If public abuse appears, add a gateway API key or same-origin restriction. Do not require MarketMafioso's inventory/acquisition client key for this metadata route; that key is private plugin-server auth and should not be embedded in browser JavaScript.

## Error Behavior

Explicit failures are preferred:

- Empty query: `400`.
- Query under minimum useful length, except numeric ID: `400`.
- Unresolved item ID: `404`.
- Provider unavailable with no usable cache: `503`.
- Provider returned malformed data: `502`.

Responses should include a short machine-readable error code and a human-readable message.

## MarketMafioso Dashboard Integration

The Market Acquisition dashboard should call the gateway from browser JavaScript for suggestions. When staging the form, the MarketMafioso server must still validate that each submitted queue row includes a resolved `itemId` and `itemName`.

The dashboard should not trust typed display text as identity. The hidden item ID comes only from selecting a gateway result or resolving a numeric item ID.

Queue builder behavior:

- Left side: item search and row controls.
- Right side: queued acquisition rows.
- `Add to Queue` requires resolved item, quantity, max unit price, and gil cap.
- `Stage Queue` posts one acquisition request per queued row.
- Existing pickup flow remains unchanged.

## Extraction Criteria

Keep the gateway inside Craft Architect backend only until one of these is true:

- MarketMafioso self-hosted deployment needs an official packaged gateway.
- Another hosted tool needs the same routes with a different deploy cadence.
- Provider/cache code grows beyond item identity and metadata.
- We need independent rate limits, auth, health checks, or observability.

When extracted, preserve the `/api/xivdata/...` public contract and move the implementation behind a dedicated service host.

## Non-Goals

- No recipe planning routes in the first slice.
- No vendor price routes in the first slice.
- No Universalis market listing routes in the first slice.
- No direct dependency from MarketMafioso to Craft Architect assemblies.
- No hidden fallback from MarketMafioso to Garland if the gateway is missing.
- No public multi-user account system for this gateway.
