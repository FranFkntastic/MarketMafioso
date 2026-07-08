# Gil Vendor Buying Engine Design

## Context

GatherBuddy Reborn's Vulcan subsystem contains useful vendor-buying behavior, but it is wrapped in a broad crafting/vendor automation surface with special-shop handling, currency handling, route preferences, buy-list management, and UI concerns that MarketMafioso does not need.

MarketMafioso should cannibalize only the functional executive ideas needed for ordinary gil NPC vendor purchases. This is an engine-only slice. It does not add a Market Acquisition integration, dashboard route support, or a normal user-facing workflow yet.

## Goals

- Add a compact `GilVendorBuying` engine module that can later be driven by MarketMafioso controls.
- Make invalid non-gil requests unrepresentable by creating executable requests only from known gil vendor offers.
- Keep the implementation subtractive: no copied Vulcan surface area unless it directly supports ordinary gil-shop purchase execution.
- Keep game/UI interaction behind a narrow adapter so planning and session behavior are testable without Dalamud.
- Produce explicit diagnostics for live execution failures.

## Non-Goals

- No special shops.
- No scrip, tomestone, seal, token, inclusion, exchange, or other currency vendors.
- No GC exchange.
- No vendor buy-list manager.
- No route preference system.
- No Market Acquisition, Acquisition Workbench, dashboard, or server integration in this slice.
- No broad vendor UI clone from Vulcan.

## Design Principle

The module should not accept "any vendor item" and then decide whether it can buy it. Instead, it should accept only catalog-derived ordinary gil offers. If an item is not known to be available from an ordinary gil NPC shop, the engine should have no request to run.

Runtime guards still exist, but they are diagnostics for stale or mismatched live state, not support for alternative vendor types.

## Module Boundary

Add a new plugin-side module under `src/MarketMafioso/GilVendorBuying/`.

The module owns:

- catalog models for ordinary gil vendor offers;
- request and plan models;
- a small state machine for a single purchase attempt;
- live-shop row normalization;
- executor orchestration against a game adapter;
- diagnostic status objects.

The module does not own:

- route creation;
- market-board purchasing;
- server persistence;
- dashboard request lifecycle;
- public UI placement.

## Core Models

`GilVendorOffer`

- `ItemId`
- `ItemName`
- `VendorId`
- `VendorName`
- `TerritoryId`
- `Position`
- `UnitPriceGil`
- optional shop/menu identifiers if needed by live execution

`GilVendorCatalog`

- Resolves candidate gil offers by item id.
- Filters at construction/import time so only ordinary gil offers are exposed.
- Is the only path for creating executable vendor-buy requests.

`GilVendorBuyRequest`

- Contains a selected `GilVendorOffer`.
- Contains requested quantity.
- May include a max total gil guard derived from `UnitPriceGil * Quantity`.

`GilVendorShopRow`

- Live normalized row from the currently open ordinary shop.
- Contains row index, item id, item name, unit price gil, and row availability data if exposed.

`GilVendorBuyResult`

- `Status`
- `Message`
- optional diagnostic dictionary
- optional purchased quantity and spent gil when confidently known

## Runtime Flow

The v1 session handles one requested offer at a time:

1. `Idle`
2. `OpenVendor`
3. `ReadGilShop`
4. `SelectOffer`
5. `ConfirmQuantity`
6. `Complete` or `Failed`

`OpenVendor` asks the game adapter to interact with the expected vendor. If the vendor or expected normal shop cannot be opened, the session fails with a diagnostic status.

`ReadGilShop` asks the adapter for ordinary gil-shop rows. If no normal gil-shop UI is readable, the session fails. It does not attempt special-shop fallbacks.

`SelectOffer` matches the requested catalog offer against live rows by item id and unit gil price. If there are multiple matching rows, prefer an exact vendor/shop row match when metadata exists; otherwise choose the first exact item/price match.

`ConfirmQuantity` selects the row, sets or accepts the requested quantity, and confirms the purchase. If the game UI presents a confirmation prompt, the adapter confirms only the expected ordinary purchase prompt.

## Game Adapter

Define an interface such as `IGilVendorBuyingGameAdapter`:

- `OpenVendor(GilVendorOffer offer)`
- `ReadOpenGilShopRows()`
- `SelectShopRow(GilVendorShopRow row)`
- `SetPurchaseQuantity(uint quantity)`
- `ConfirmPurchase()`
- `CaptureDiagnostics()`

The first implementation can be `DalamudGilVendorBuyingGameAdapter`. It is the only layer allowed to use FFXIVClientStructs, addon pointers, callbacks, target interaction, or Dalamud object tables.

Vulcan should be used as a reference for addon names, callback payload shapes, quantity dialog handling, and confirmation sequencing. The implementation should not port Vulcan's broad vendor manager classes wholesale.

## Catalog Source

The catalog can begin as a thin static/imported projection of ordinary gil offers. The important v1 requirement is not perfect coverage; it is clean filtering.

Acceptable first sources:

- a generated or checked-in data file containing known ordinary gil vendor offers;
- a small hand-seeded catalog for initial live proving;
- a later data-import step once the executor shape is stable.

The catalog should expose only known gil offers. Unknown items are not executable.

## Diagnostics

Every failure returns a stable status and a plain explanation:

- `VendorUnavailable`
- `VendorOpenFailed`
- `GilShopNotOpen`
- `OfferNotInLiveShop`
- `PriceMismatch`
- `InsufficientGil`
- `QuantityRejected`
- `ConfirmationUnavailable`
- `PurchaseOutcomeUnknown`

Diagnostics should include enough data for live troubleshooting: expected item id/name, expected vendor id/name, expected price, live row count, matched row details, and visible addon names when available.

## Testing

Unit tests should cover:

- catalog-derived requests cannot be created for non-catalog items;
- request validation rejects zero quantity and overflowed gil totals;
- session transitions for success;
- failure statuses for missing vendor, unreadable shop, missing row, price mismatch, quantity rejection, and confirmation failure;
- live row matching by item id and unit price.

Adapter behavior should be kept thin enough that most tests use fake adapters. Live Dalamud behavior will be verified manually after implementation through a temporary diagnostic entry point or dev-only harness.

## Future Integration

Once the engine is proven, later slices can decide how to drive it:

- hidden diagnostic command or window;
- Acquisition Workbench vendor-source line;
- Market Acquisition route step for known gil-vendor materials;
- Workshop/Restock helper for known vendor-purchasable materials.

Those integrations should use the engine API instead of reaching into adapter internals.

## Acceptance Criteria

- A new isolated module exists for gil vendor buying.
- The executable request path starts from a known ordinary gil vendor offer.
- No special-shop or currency-vendor branches are present.
- Core planning/session behavior is covered by tests.
- The live adapter is narrow and easy to replace or disable.
- No Market Acquisition or dashboard workflow changes are included in this slice.
