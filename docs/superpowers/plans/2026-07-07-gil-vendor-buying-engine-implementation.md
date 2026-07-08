# Gil Vendor Buying Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an isolated, engine-only `GilVendorBuying` module that can create catalog-gated ordinary gil NPC vendor purchase requests and run the purchase session against a testable adapter boundary without wiring into Market Acquisition or public UI.

**Architecture:** The module makes executable requests only from known `GilVendorOffer` catalog entries, so non-gil vendor automation is not representable. Pure models, catalog validation, row matching, and session state transitions are testable with fake adapters; the live Dalamud adapter starts as a narrow, fail-closed boundary for future ordinary-gil-shop UI interaction.

**Tech Stack:** C# 12, .NET 10, Dalamud.NET.Sdk 15, xUnit, FFXIVClientStructs through the existing plugin project.

---

## File Structure

Create `src/MarketMafioso/GilVendorBuying/`:

- `GilVendorOffer.cs`: immutable catalog offer model and vector-light vendor position.
- `GilVendorBuyRequest.cs`: catalog-derived executable request with quantity and gil guard validation.
- `GilVendorCatalog.cs`: in-memory ordinary-gil-offer catalog and request factory.
- `GilVendorShopRow.cs`: normalized live ordinary shop row model.
- `GilVendorBuyResult.cs`: stable result/status model plus diagnostic details.
- `GilVendorShopMatcher.cs`: pure row matching logic by item id, gil price, and optional shop item id.
- `IGilVendorBuyingGameAdapter.cs`: narrow adapter boundary for live game/UI operations.
- `GilVendorBuyingSession.cs`: one-request state machine and orchestration.
- `DalamudGilVendorBuyingGameAdapter.cs`: compact fail-closed adapter shell for ordinary gil-shop UI diagnostics; no special-shop branches.

Create `tests/MarketMafioso.Tests/GilVendorBuying/`:

- `GilVendorCatalogTests.cs`
- `GilVendorBuyRequestTests.cs`
- `GilVendorShopMatcherTests.cs`
- `GilVendorBuyingSessionTests.cs`

Do not modify `MainWindow`, Market Acquisition classes, dashboard/server projects, or plugin config in this slice.

---

### Task 1: Add Core Offer, Request, and Result Models

**Files:**
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorOffer.cs`
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorBuyRequest.cs`
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorBuyResult.cs`
- Test: `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyRequestTests.cs`

- [ ] **Step 1: Write failing request validation tests**

Create `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyRequestTests.cs`:

```csharp
namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorBuyRequestTests
{
    [Fact]
    public void Create_rejects_zero_quantity()
    {
        var offer = CreateOffer();

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 0);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidQuantity", result.Status);
        Assert.Contains("greater than zero", result.Message);
    }

    [Fact]
    public void Create_rejects_total_gil_overflow()
    {
        var offer = CreateOffer(unitPriceGil: uint.MaxValue);

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, uint.MaxValue);

        Assert.False(result.IsSuccess);
        Assert.Equal("GilTotalOverflow", result.Status);
    }

    [Fact]
    public void Create_returns_request_with_expected_total_gil()
    {
        var offer = CreateOffer(unitPriceGil: 216);

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 12);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Request);
        Assert.Equal(12u, result.Request.Quantity);
        Assert.Equal(2_592UL, result.Request.MaxTotalGil);
        Assert.Equal(216u, result.Request.Offer.UnitPriceGil);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorOffer CreateOffer(uint unitPriceGil = 216) =>
        new(
            ItemId: 2_002,
            ItemName: "Fire Shard",
            VendorId: 10_012,
            VendorName: "Material Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(12.5f, 0f, -22.25f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 50_001);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorBuyRequestTests"
```

Expected: compile failure because `MarketMafioso.GilVendorBuying` types do not exist.

- [ ] **Step 3: Add offer model**

Create `src/MarketMafioso/GilVendorBuying/GilVendorOffer.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorPosition(float X, float Y, float Z);

public sealed record GilVendorOffer(
    uint ItemId,
    string ItemName,
    uint VendorId,
    string VendorName,
    uint TerritoryId,
    GilVendorPosition Position,
    uint UnitPriceGil,
    uint? ShopItemId = null)
{
    public bool IsValidOrdinaryGilOffer =>
        ItemId != 0 &&
        !string.IsNullOrWhiteSpace(ItemName) &&
        VendorId != 0 &&
        !string.IsNullOrWhiteSpace(VendorName) &&
        TerritoryId != 0 &&
        UnitPriceGil > 0;
}
```

- [ ] **Step 4: Add request model and factory**

Create `src/MarketMafioso/GilVendorBuying/GilVendorBuyRequest.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorBuyRequest(GilVendorOffer Offer, uint Quantity, ulong MaxTotalGil)
{
    public static GilVendorBuyRequestCreateResult Create(GilVendorOffer offer, uint quantity)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (!offer.IsValidOrdinaryGilOffer)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "InvalidOffer",
                "The selected vendor offer is not a valid ordinary gil offer.");
        }

        if (quantity == 0)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "InvalidQuantity",
                "Vendor buy quantity must be greater than zero.");
        }

        var total = checked((ulong)offer.UnitPriceGil * quantity);
        if (total > int.MaxValue)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "GilTotalOverflow",
                "Vendor buy total exceeds the supported gil guard for one purchase attempt.");
        }

        return GilVendorBuyRequestCreateResult.Success(new GilVendorBuyRequest(offer, quantity, total));
    }
}

public sealed record GilVendorBuyRequestCreateResult(
    bool IsSuccess,
    string Status,
    string Message,
    GilVendorBuyRequest? Request)
{
    public static GilVendorBuyRequestCreateResult Success(GilVendorBuyRequest request) =>
        new(true, "Ready", "Vendor buy request is ready.", request);

    public static GilVendorBuyRequestCreateResult Fail(string status, string message) =>
        new(false, status, message, null);
}
```

- [ ] **Step 5: Add buy result model**

Create `src/MarketMafioso/GilVendorBuying/GilVendorBuyResult.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorBuyResult(
    bool IsSuccess,
    string Status,
    string Message,
    IReadOnlyDictionary<string, string?> Details,
    uint PurchasedQuantity = 0,
    ulong SpentGil = 0)
{
    public static GilVendorBuyResult Success(
        string message,
        uint purchasedQuantity,
        ulong spentGil,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(true, "Complete", message, details ?? new Dictionary<string, string?>(), purchasedQuantity, spentGil);

    public static GilVendorBuyResult Fail(
        string status,
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(false, status, message, details ?? new Dictionary<string, string?>());
}
```

- [ ] **Step 6: Run focused tests and verify they pass**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorBuyRequestTests"
```

Expected: all `GilVendorBuyRequestTests` pass.

- [ ] **Step 7: Commit Task 1**

```powershell
git add -- src/MarketMafioso/GilVendorBuying/GilVendorOffer.cs src/MarketMafioso/GilVendorBuying/GilVendorBuyRequest.cs src/MarketMafioso/GilVendorBuying/GilVendorBuyResult.cs tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyRequestTests.cs
git commit -m "feat: add gil vendor buy request models"
```

---

### Task 2: Add Catalog-Gated Request Creation

**Files:**
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorCatalog.cs`
- Test: `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorCatalogTests.cs`

- [ ] **Step 1: Write failing catalog tests**

Create `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorCatalogTests.cs`:

```csharp
namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorCatalogTests
{
    [Fact]
    public void Create_ignores_invalid_non_gil_offers()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create(
        [
            CreateOffer(itemId: 1, unitPriceGil: 0),
            CreateOffer(itemId: 2, unitPriceGil: 8),
        ]);

        Assert.Empty(catalog.FindOffersByItemId(1));
        Assert.Single(catalog.FindOffersByItemId(2));
    }

    [Fact]
    public void TryCreateRequest_fails_when_item_has_no_catalog_offer()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create([CreateOffer(itemId: 2)]);

        var result = catalog.TryCreateRequest(itemId: 3, quantity: 1);

        Assert.False(result.IsSuccess);
        Assert.Equal("OfferNotCataloged", result.Status);
        Assert.Null(result.Request);
    }

    [Fact]
    public void TryCreateRequest_uses_preferred_vendor_when_available()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create(
        [
            CreateOffer(itemId: 2, vendorId: 10),
            CreateOffer(itemId: 2, vendorId: 99),
        ]);

        var result = catalog.TryCreateRequest(itemId: 2, quantity: 4, preferredVendorId: 99);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Request);
        Assert.Equal(99u, result.Request.Offer.VendorId);
        Assert.Equal(4u, result.Request.Quantity);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorOffer CreateOffer(
        uint itemId,
        uint vendorId = 10,
        uint unitPriceGil = 8) =>
        new(
            ItemId: itemId,
            ItemName: $"Item {itemId}",
            VendorId: vendorId,
            VendorName: $"Vendor {vendorId}",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(1f, 2f, 3f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 40_000 + itemId);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorCatalogTests"
```

Expected: compile failure because `GilVendorCatalog` does not exist.

- [ ] **Step 3: Implement catalog**

Create `src/MarketMafioso/GilVendorBuying/GilVendorCatalog.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed class GilVendorCatalog
{
    private readonly Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId;

    private GilVendorCatalog(Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId)
    {
        this.offersByItemId = offersByItemId;
    }

    public static GilVendorCatalog Create(IEnumerable<GilVendorOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);

        var grouped = offers
            .Where(offer => offer.IsValidOrdinaryGilOffer)
            .GroupBy(offer => offer.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GilVendorOffer>)group
                    .OrderBy(offer => offer.UnitPriceGil)
                    .ThenBy(offer => offer.VendorName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.VendorId)
                    .ToList());

        return new GilVendorCatalog(grouped);
    }

    public IReadOnlyList<GilVendorOffer> FindOffersByItemId(uint itemId) =>
        offersByItemId.TryGetValue(itemId, out var offers) ? offers : [];

    public GilVendorBuyRequestCreateResult TryCreateRequest(
        uint itemId,
        uint quantity,
        uint? preferredVendorId = null)
    {
        var offers = FindOffersByItemId(itemId);
        if (offers.Count == 0)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "OfferNotCataloged",
                $"Item {itemId} is not known to be sold by an ordinary gil vendor.");
        }

        var offer = preferredVendorId is { } vendorId
            ? offers.FirstOrDefault(candidate => candidate.VendorId == vendorId)
            : offers[0];

        if (offer == null)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "PreferredVendorUnavailable",
                $"Item {itemId} is not known to be sold by ordinary gil vendor {preferredVendorId}.");
        }

        return GilVendorBuyRequest.Create(offer, quantity);
    }
}
```

- [ ] **Step 4: Run focused tests and verify they pass**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorCatalogTests"
```

Expected: all catalog tests pass.

- [ ] **Step 5: Run Task 1 and Task 2 tests together**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendor"
```

Expected: all current `GilVendorBuying` tests pass.

- [ ] **Step 6: Commit Task 2**

```powershell
git add -- src/MarketMafioso/GilVendorBuying/GilVendorCatalog.cs tests/MarketMafioso.Tests/GilVendorBuying/GilVendorCatalogTests.cs
git commit -m "feat: gate vendor buy requests behind gil catalog"
```

---

### Task 3: Add Live Gil-Shop Row Matching

**Files:**
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorShopRow.cs`
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorShopMatcher.cs`
- Test: `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorShopMatcherTests.cs`

- [ ] **Step 1: Write failing matcher tests**

Create `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorShopMatcherTests.cs`:

```csharp
namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorShopMatcherTests
{
    [Fact]
    public void FindMatchingRow_returns_exact_item_price_and_shop_item_match()
    {
        var request = CreateRequest(shopItemId: 777);
        var rows = new[]
        {
            CreateRow(rowIndex: 0, itemId: 2_002, unitPriceGil: 216, shopItemId: 111),
            CreateRow(rowIndex: 1, itemId: 2_002, unitPriceGil: 216, shopItemId: 777),
        };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.True(match.IsSuccess);
        Assert.NotNull(match.Row);
        Assert.Equal(1, match.Row.RowIndex);
        Assert.Equal("Matched requested gil vendor offer in the live shop.", match.Message);
    }

    [Fact]
    public void FindMatchingRow_rejects_price_mismatch()
    {
        var request = CreateRequest(unitPriceGil: 216);
        var rows = new[] { CreateRow(rowIndex: 0, itemId: 2_002, unitPriceGil: 999) };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.False(match.IsSuccess);
        Assert.Equal("PriceMismatch", match.Status);
        Assert.Null(match.Row);
    }

    [Fact]
    public void FindMatchingRow_reports_missing_offer_when_item_is_absent()
    {
        var request = CreateRequest();
        var rows = new[] { CreateRow(rowIndex: 0, itemId: 9_999, unitPriceGil: 216) };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.False(match.IsSuccess);
        Assert.Equal("OfferNotInLiveShop", match.Status);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorBuyRequest CreateRequest(
        uint unitPriceGil = 216,
        uint? shopItemId = 777)
    {
        var offer = new MarketMafioso.GilVendorBuying.GilVendorOffer(
            ItemId: 2_002,
            ItemName: "Fire Shard",
            VendorId: 10_012,
            VendorName: "Material Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(12.5f, 0f, -22.25f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: shopItemId);

        return MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 3).Request!;
    }

    private static MarketMafioso.GilVendorBuying.GilVendorShopRow CreateRow(
        int rowIndex,
        uint itemId,
        uint unitPriceGil,
        uint? shopItemId = null) =>
        new(
            RowIndex: rowIndex,
            ItemId: itemId,
            ItemName: $"Item {itemId}",
            UnitPriceGil: unitPriceGil,
            ShopItemId: shopItemId,
            AvailableQuantity: null);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorShopMatcherTests"
```

Expected: compile failure because shop row and matcher types do not exist.

- [ ] **Step 3: Add shop row model**

Create `src/MarketMafioso/GilVendorBuying/GilVendorShopRow.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorShopRow(
    int RowIndex,
    uint ItemId,
    string ItemName,
    uint UnitPriceGil,
    uint? ShopItemId,
    uint? AvailableQuantity);
```

- [ ] **Step 4: Add matcher**

Create `src/MarketMafioso/GilVendorBuying/GilVendorShopMatcher.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public static class GilVendorShopMatcher
{
    public static GilVendorShopMatchResult FindMatchingRow(
        GilVendorBuyRequest request,
        IReadOnlyList<GilVendorShopRow> rows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var itemRows = rows.Where(row => row.ItemId == request.Offer.ItemId).ToList();
        if (itemRows.Count == 0)
        {
            return GilVendorShopMatchResult.Fail(
                "OfferNotInLiveShop",
                "The open gil shop does not contain the requested catalog offer.",
                request,
                rows);
        }

        var priceRows = itemRows.Where(row => row.UnitPriceGil == request.Offer.UnitPriceGil).ToList();
        if (priceRows.Count == 0)
        {
            return GilVendorShopMatchResult.Fail(
                "PriceMismatch",
                "The open gil shop contains the requested item, but not at the catalog gil price.",
                request,
                rows);
        }

        var exactShopItem = request.Offer.ShopItemId is { } shopItemId
            ? priceRows.FirstOrDefault(row => row.ShopItemId == shopItemId)
            : null;
        var selected = exactShopItem ?? priceRows.OrderBy(row => row.RowIndex).First();

        return GilVendorShopMatchResult.Success(
            selected,
            "Matched requested gil vendor offer in the live shop.",
            new Dictionary<string, string?>
            {
                ["expectedItemId"] = request.Offer.ItemId.ToString(),
                ["expectedItemName"] = request.Offer.ItemName,
                ["expectedUnitPriceGil"] = request.Offer.UnitPriceGil.ToString(),
                ["matchedRowIndex"] = selected.RowIndex.ToString(),
                ["matchedShopItemId"] = selected.ShopItemId?.ToString(),
                ["liveRowCount"] = rows.Count.ToString(),
            });
    }
}

public sealed record GilVendorShopMatchResult(
    bool IsSuccess,
    string Status,
    string Message,
    GilVendorShopRow? Row,
    IReadOnlyDictionary<string, string?> Details)
{
    public static GilVendorShopMatchResult Success(
        GilVendorShopRow row,
        string message,
        IReadOnlyDictionary<string, string?> details) =>
        new(true, "Ready", message, row, details);

    public static GilVendorShopMatchResult Fail(
        string status,
        string message,
        GilVendorBuyRequest request,
        IReadOnlyList<GilVendorShopRow> rows) =>
        new(
            false,
            status,
            message,
            null,
            new Dictionary<string, string?>
            {
                ["expectedItemId"] = request.Offer.ItemId.ToString(),
                ["expectedItemName"] = request.Offer.ItemName,
                ["expectedUnitPriceGil"] = request.Offer.UnitPriceGil.ToString(),
                ["liveRowCount"] = rows.Count.ToString(),
            });
}
```

- [ ] **Step 5: Run matcher tests and verify they pass**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorShopMatcherTests"
```

Expected: all matcher tests pass.

- [ ] **Step 6: Run all module tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendor"
```

Expected: all `GilVendorBuying` tests pass.

- [ ] **Step 7: Commit Task 3**

```powershell
git add -- src/MarketMafioso/GilVendorBuying/GilVendorShopRow.cs src/MarketMafioso/GilVendorBuying/GilVendorShopMatcher.cs tests/MarketMafioso.Tests/GilVendorBuying/GilVendorShopMatcherTests.cs
git commit -m "feat: match live gil vendor shop rows"
```

---

### Task 4: Add Adapter Boundary and Session State Machine

**Files:**
- Create: `src/MarketMafioso/GilVendorBuying/IGilVendorBuyingGameAdapter.cs`
- Create: `src/MarketMafioso/GilVendorBuying/GilVendorBuyingSession.cs`
- Test: `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyingSessionTests.cs`

- [ ] **Step 1: Write failing session tests**

Create `tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyingSessionTests.cs`:

```csharp
namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorBuyingSessionTests
{
    [Fact]
    public void Run_completes_one_catalog_request()
    {
        var request = CreateRequest(quantity: 5, unitPriceGil: 12);
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            Rows =
            [
                new MarketMafioso.GilVendorBuying.GilVendorShopRow(0, request.Offer.ItemId, request.Offer.ItemName, 12, request.Offer.ShopItemId, null),
            ],
            SelectRowResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Row selected.", 0, 0),
            SetQuantityResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Quantity set.", 0, 0),
            ConfirmResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Purchase confirmed.", 5, 60),
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("Complete", result.Status);
        Assert.Equal(MarketMafioso.GilVendorBuying.GilVendorBuyingSessionState.Complete, session.State);
        Assert.Equal(5u, result.PurchasedQuantity);
        Assert.Equal(60UL, result.SpentGil);
        Assert.Equal(1, adapter.OpenVendorCalls);
        Assert.Equal(1, adapter.ReadRowsCalls);
        Assert.Equal(1, adapter.SelectRowCalls);
        Assert.Equal(1, adapter.SetQuantityCalls);
        Assert.Equal(1, adapter.ConfirmCalls);
    }

    [Fact]
    public void Run_fails_when_shop_is_not_readable()
    {
        var request = CreateRequest();
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            ReadRowsResult = MarketMafioso.GilVendorBuying.GilVendorShopReadResult.Fail("GilShopNotOpen", "Normal gil shop is not open."),
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("GilShopNotOpen", result.Status);
        Assert.Equal(MarketMafioso.GilVendorBuying.GilVendorBuyingSessionState.Failed, session.State);
        Assert.Equal(0, adapter.SelectRowCalls);
    }

    [Fact]
    public void Run_fails_when_live_row_price_mismatches_catalog()
    {
        var request = CreateRequest(unitPriceGil: 12);
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            Rows =
            [
                new MarketMafioso.GilVendorBuying.GilVendorShopRow(0, request.Offer.ItemId, request.Offer.ItemName, 99, request.Offer.ShopItemId, null),
            ],
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("PriceMismatch", result.Status);
        Assert.Equal(0, adapter.SelectRowCalls);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorBuyRequest CreateRequest(
        uint quantity = 1,
        uint unitPriceGil = 12)
    {
        var offer = new MarketMafioso.GilVendorBuying.GilVendorOffer(
            ItemId: 3_111,
            ItemName: "Bronze Ingot",
            VendorId: 88,
            VendorName: "Tools Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(1f, 2f, 3f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 44_444);
        return MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, quantity).Request!;
    }

    private sealed class FakeAdapter : MarketMafioso.GilVendorBuying.IGilVendorBuyingGameAdapter
    {
        public int OpenVendorCalls { get; private set; }
        public int ReadRowsCalls { get; private set; }
        public int SelectRowCalls { get; private set; }
        public int SetQuantityCalls { get; private set; }
        public int ConfirmCalls { get; private set; }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult OpenVendorResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0);

        public IReadOnlyList<MarketMafioso.GilVendorBuying.GilVendorShopRow> Rows { get; init; } = [];

        public MarketMafioso.GilVendorBuying.GilVendorShopReadResult? ReadRowsResult { get; init; }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SelectRowResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Row selected.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SetQuantityResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Quantity set.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult ConfirmResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Purchase confirmed.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult OpenVendor(MarketMafioso.GilVendorBuying.GilVendorOffer offer)
        {
            OpenVendorCalls++;
            return OpenVendorResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorShopReadResult ReadOpenGilShopRows()
        {
            ReadRowsCalls++;
            return ReadRowsResult ?? MarketMafioso.GilVendorBuying.GilVendorShopReadResult.Success(Rows);
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SelectShopRow(MarketMafioso.GilVendorBuying.GilVendorShopRow row)
        {
            SelectRowCalls++;
            return SelectRowResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SetPurchaseQuantity(uint quantity)
        {
            SetQuantityCalls++;
            return SetQuantityResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult ConfirmPurchase()
        {
            ConfirmCalls++;
            return ConfirmResult;
        }

        public IReadOnlyDictionary<string, string?> CaptureDiagnostics() =>
            new Dictionary<string, string?> { ["adapter"] = "fake" };
    }
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorBuyingSessionTests"
```

Expected: compile failure because adapter/session types do not exist.

- [ ] **Step 3: Add adapter interface and shop read result**

Create `src/MarketMafioso/GilVendorBuying/IGilVendorBuyingGameAdapter.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public interface IGilVendorBuyingGameAdapter
{
    GilVendorBuyResult OpenVendor(GilVendorOffer offer);
    GilVendorShopReadResult ReadOpenGilShopRows();
    GilVendorBuyResult SelectShopRow(GilVendorShopRow row);
    GilVendorBuyResult SetPurchaseQuantity(uint quantity);
    GilVendorBuyResult ConfirmPurchase();
    IReadOnlyDictionary<string, string?> CaptureDiagnostics();
}

public sealed record GilVendorShopReadResult(
    bool IsSuccess,
    string Status,
    string Message,
    IReadOnlyList<GilVendorShopRow> Rows,
    IReadOnlyDictionary<string, string?> Details)
{
    public static GilVendorShopReadResult Success(
        IReadOnlyList<GilVendorShopRow> rows,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(true, "Ready", "Read ordinary gil shop rows.", rows, details ?? new Dictionary<string, string?>());

    public static GilVendorShopReadResult Fail(
        string status,
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(false, status, message, [], details ?? new Dictionary<string, string?>());
}
```

- [ ] **Step 4: Add session state machine**

Create `src/MarketMafioso/GilVendorBuying/GilVendorBuyingSession.cs`:

```csharp
namespace MarketMafioso.GilVendorBuying;

public sealed class GilVendorBuyingSession
{
    private readonly IGilVendorBuyingGameAdapter adapter;

    public GilVendorBuyingSession(IGilVendorBuyingGameAdapter adapter)
    {
        this.adapter = adapter;
    }

    public GilVendorBuyingSessionState State { get; private set; } = GilVendorBuyingSessionState.Idle;

    public GilVendorBuyResult Run(GilVendorBuyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        State = GilVendorBuyingSessionState.OpenVendor;
        var open = adapter.OpenVendor(request.Offer);
        if (!open.IsSuccess)
            return Fail(open);

        State = GilVendorBuyingSessionState.ReadGilShop;
        var read = adapter.ReadOpenGilShopRows();
        if (!read.IsSuccess)
            return Fail(GilVendorBuyResult.Fail(read.Status, read.Message, MergeDetails(read.Details)));

        var match = GilVendorShopMatcher.FindMatchingRow(request, read.Rows);
        if (!match.IsSuccess || match.Row == null)
            return Fail(GilVendorBuyResult.Fail(match.Status, match.Message, MergeDetails(match.Details)));

        State = GilVendorBuyingSessionState.SelectOffer;
        var select = adapter.SelectShopRow(match.Row);
        if (!select.IsSuccess)
            return Fail(select);

        State = GilVendorBuyingSessionState.ConfirmQuantity;
        var quantity = adapter.SetPurchaseQuantity(request.Quantity);
        if (!quantity.IsSuccess)
            return Fail(quantity);

        var confirm = adapter.ConfirmPurchase();
        if (!confirm.IsSuccess)
            return Fail(confirm);

        State = GilVendorBuyingSessionState.Complete;
        return confirm;
    }

    private GilVendorBuyResult Fail(GilVendorBuyResult result)
    {
        State = GilVendorBuyingSessionState.Failed;
        return result;
    }

    private IReadOnlyDictionary<string, string?> MergeDetails(IReadOnlyDictionary<string, string?> details)
    {
        var merged = new Dictionary<string, string?>(details);
        foreach (var pair in adapter.CaptureDiagnostics())
            merged.TryAdd(pair.Key, pair.Value);
        return merged;
    }
}

public enum GilVendorBuyingSessionState
{
    Idle,
    OpenVendor,
    ReadGilShop,
    SelectOffer,
    ConfirmQuantity,
    Complete,
    Failed,
}
```

- [ ] **Step 5: Run session tests and verify they pass**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendorBuyingSessionTests"
```

Expected: all session tests pass.

- [ ] **Step 6: Run all module tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendor"
```

Expected: all `GilVendorBuying` tests pass.

- [ ] **Step 7: Commit Task 4**

```powershell
git add -- src/MarketMafioso/GilVendorBuying/IGilVendorBuyingGameAdapter.cs src/MarketMafioso/GilVendorBuying/GilVendorBuyingSession.cs tests/MarketMafioso.Tests/GilVendorBuying/GilVendorBuyingSessionTests.cs
git commit -m "feat: add gil vendor buying session engine"
```

---

### Task 5: Add Thin Dalamud Adapter Shell

**Files:**
- Create: `src/MarketMafioso/GilVendorBuying/DalamudGilVendorBuyingGameAdapter.cs`
- Modify only if compile requires using aliases: no other project files should need changes.

- [ ] **Step 1: Add compile-focused adapter shell**

Create `src/MarketMafioso/GilVendorBuying/DalamudGilVendorBuyingGameAdapter.cs`:

```csharp
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.GilVendorBuying;

public sealed class DalamudGilVendorBuyingGameAdapter : IGilVendorBuyingGameAdapter
{
    private const string GilShopAddonName = "Shop";
    private const string QuantityAddonName = "InputNumeric";
    private const string ConfirmAddonName = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Dictionary<string, string?> diagnostics = new();

    public DalamudGilVendorBuyingGameAdapter(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public GilVendorBuyResult OpenVendor(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        diagnostics["expectedVendorId"] = offer.VendorId.ToString();
        diagnostics["expectedVendorName"] = offer.VendorName;
        diagnostics["expectedTerritoryId"] = offer.TerritoryId.ToString();

        return GilVendorBuyResult.Fail(
            "VendorOpenFailed",
            "Live gil vendor interaction is not wired to a control surface yet; open the ordinary gil shop manually before using the engine harness.",
            CaptureDiagnostics());
    }

    public unsafe GilVendorShopReadResult ReadOpenGilShopRows()
    {
        var shop = gameGui.GetAddonByName<AtkUnitBase>(GilShopAddonName, 1);
        if (!IsAddonReady(shop))
        {
            diagnostics["gilShopAddon"] = "missing";
            return GilVendorShopReadResult.Fail(
                "GilShopNotOpen",
                "The ordinary gil shop addon is not open or not ready.",
                CaptureDiagnostics());
        }

        diagnostics["gilShopAddon"] = "ready";
        log.Debug("[MarketMafioso] Ordinary gil shop addon is visible; row extraction will be implemented after live addon shape capture.");
        return GilVendorShopReadResult.Fail(
            "GilShopRowsUnavailable",
            "The ordinary gil shop is open, but row extraction has not been implemented for this addon shape yet.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult SelectShopRow(GilVendorShopRow row)
    {
        diagnostics["selectedRowIndex"] = row.RowIndex.ToString();
        return GilVendorBuyResult.Fail(
            "SelectionUnavailable",
            "Live ordinary gil shop row selection is disabled in this engine-only slice.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult SetPurchaseQuantity(uint quantity)
    {
        diagnostics["requestedQuantity"] = quantity.ToString();
        diagnostics["quantityAddon"] = IsAddonVisible(QuantityAddonName) ? "visible" : "missing";
        return GilVendorBuyResult.Fail(
            "QuantityRejected",
            "Live ordinary gil shop quantity entry is disabled in this engine-only slice.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult ConfirmPurchase()
    {
        diagnostics["confirmAddon"] = IsAddonVisible(ConfirmAddonName) ? "visible" : "missing";
        return GilVendorBuyResult.Fail(
            "ConfirmationUnavailable",
            "Live ordinary gil shop purchase confirmation is disabled in this engine-only slice.",
            CaptureDiagnostics());
    }

    public IReadOnlyDictionary<string, string?> CaptureDiagnostics() =>
        new Dictionary<string, string?>(diagnostics);

    private unsafe bool IsAddonVisible(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsVisible;
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon) =>
        addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;
}
```

This shell intentionally fails closed for live execution. It establishes the narrow adapter boundary and addon diagnostics without pretending row callbacks are safe before live capture.

- [ ] **Step 2: Build plugin project**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj -c Debug
```

Expected: build succeeds. Debug build may sync the dev plugin through the existing MSBuild target.

- [ ] **Step 3: Run all module tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendor"
```

Expected: all `GilVendorBuying` tests pass.

- [ ] **Step 4: Commit Task 5**

```powershell
git add -- src/MarketMafioso/GilVendorBuying/DalamudGilVendorBuyingGameAdapter.cs
git commit -m "feat: add gil vendor buying game adapter boundary"
```

---

### Task 6: Final Verification and Scope Guard

**Files:**
- Modify: `docs/superpowers/plans/2026-07-07-gil-vendor-buying-engine-implementation.md` only if implementation discoveries require plan correction before execution completes.

- [ ] **Step 1: Verify no forbidden integration files changed**

Run:

```powershell
git diff --name-only HEAD~5..HEAD
```

Expected output includes only:

```text
src/MarketMafioso/GilVendorBuying/...
tests/MarketMafioso.Tests/GilVendorBuying/...
```

If `src/MarketMafioso/Windows/MainWindow.cs`, `src/MarketMafioso/MarketAcquisition/...`, `src/MarketMafioso.Server/...`, or `src/MarketMafioso.Dashboard/...` appears, stop and review because this slice should not wire the engine into any workflow.

- [ ] **Step 2: Run focused module tests**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName~GilVendor"
```

Expected: all `GilVendorBuying` tests pass.

- [ ] **Step 3: Run broader plugin tests if time permits**

Run:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "FullyQualifiedName!~Server"
```

Expected: plugin test suite passes. If unrelated tests fail, capture the failing test names and errors before deciding whether they are in scope.

- [ ] **Step 4: Build Debug plugin**

Run:

```powershell
dotnet build .\src\MarketMafioso\MarketMafioso.csproj -c Debug
```

Expected: build succeeds and existing Debug dev-plugin sync completes.

- [ ] **Step 5: Summarize implementation boundary**

Record in the final implementation response:

```text
Implemented isolated GilVendorBuying engine only.
No Market Acquisition, dashboard, server, or MainWindow workflow wiring was added.
Live adapter currently fails closed for ordinary-shop row extraction until a live addon capture task wires callbacks.
```

Do not claim live vendor purchasing works until a follow-up live adapter task implements and verifies row extraction, selection, quantity entry, and confirmation against the real game UI.

---

## Self-Review

- Spec coverage: Tasks 1-4 implement catalog-gated requests, pure row matching, testable session behavior, diagnostics, and adapter boundary. Task 5 adds a fail-closed live adapter shell. Task 6 guards against accidental Market Acquisition/dashboard wiring.
- Placeholder scan: The plan contains no `TBD` or `TODO` placeholders. The adapter shell explicitly fails closed where live callback work is outside this engine-only slice.
- Type consistency: All planned types use the `MarketMafioso.GilVendorBuying` namespace and the same property/method names across tests and implementation snippets.
- Scope check: The plan intentionally stops before public controls and before live purchase callbacks. That keeps the engine in the codebase without making it drive.
