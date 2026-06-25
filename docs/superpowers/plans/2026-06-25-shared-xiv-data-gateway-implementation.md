# Shared XIV Data Gateway Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a shared item search/resolution gateway and wire MarketMafioso's acquisition dashboard to use resolved item rows instead of user-supplied item IDs.

**Architecture:** The first gateway implementation lives in the Craft Architect hosted helper service as `/xivdata/items/...` routes, but uses standalone DTO/provider/cache/service boundaries so it can be extracted later. MarketMafioso consumes the gateway from browser-side dashboard JavaScript and still validates resolved item IDs server-side before creating acquisition requests.

**Tech Stack:** ASP.NET Core minimal APIs, C# `net8.0`, xUnit, existing Craft Architect `GarlandService`, MarketMafioso ASP.NET receiver dashboard HTML/CSS/JS.

---

## Workspaces

- MarketMafioso workspace: `C:\Users\gianf\.codex\worktrees\1857\MarketMafioso`
- Craft Architect active checkout has dirty Trade work. Do not edit it directly.
- Create isolated Craft Architect worktree from `local-dev` at `C:\Users\gianf\.codex\worktrees\craftarchitect-xiv-data-gateway` on branch `feature/xiv-data-gateway`.

## Task 1: Craft Architect Gateway Contracts And Provider

**Files:**
- Create: `src/FFXIV Craft Architect.LodestoneLookup/Services/XivData/XivItemModels.cs`
- Create: `src/FFXIV Craft Architect.LodestoneLookup/Services/XivData/IXivItemDataProvider.cs`
- Create: `src/FFXIV Craft Architect.LodestoneLookup/Services/XivData/GarlandXivItemDataProvider.cs`
- Test: `src/FFXIV Craft Architect.Tests/XivDataGatewayProviderTests.cs`
- Modify: `src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj`

- [ ] **Step 1: Add failing provider tests**

Create tests that construct `GarlandXivItemDataProvider` with a fake `IGarlandService`.

Required test cases:

```csharp
[Fact]
public async Task SearchItemsAsync_MapsGarlandItemResultsToGatewaySummaries()
{
    var garland = new StubGarlandService
    {
        SearchResults =
        [
            new GarlandSearchResult
            {
                Type = "item",
                IdRaw = 5057,
                Object = new GarlandSearchObject { Name = "Darksteel Nugget", IconIdRaw = 21203 }
            }
        ]
    };
    var provider = new GarlandXivItemDataProvider(garland);

    var results = await provider.SearchItemsAsync("darksteel", 20, CancellationToken.None);

    var item = Assert.Single(results);
    Assert.Equal(5057u, item.ItemId);
    Assert.Equal("Darksteel Nugget", item.Name);
    Assert.Equal(21203, item.IconId);
    Assert.Equal("garland", item.Source);
}

[Fact]
public async Task GetItemAsync_MapsGarlandItemDetailToGatewaySummary()
{
    var garland = new StubGarlandService
    {
        Items = { [5057] = new GarlandItem { Id = 5057, Name = "Darksteel Nugget", IconId = 21203 } }
    };
    var provider = new GarlandXivItemDataProvider(garland);

    var item = await provider.GetItemAsync(5057, CancellationToken.None);

    Assert.NotNull(item);
    Assert.Equal("Darksteel Nugget", item!.Name);
    Assert.Equal(5057u, item.ItemId);
}
```

Run:

```powershell
dotnet test "src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~XivDataGatewayProviderTests" -v minimal
```

Expected: fail because gateway types do not exist.

- [ ] **Step 2: Implement models and provider**

Create `XivItemSummary`, `XivItemSearchResponse`, `XivDataErrorResponse`, `IXivItemDataProvider`, and `GarlandXivItemDataProvider`.

Provider rules:

- Reject empty query with `ArgumentException`.
- Clamp limit to `1..50`.
- Filter out Garland rows whose `Type` is not `item`.
- Filter out rows with ID `0` or blank name.
- Set `Source = "garland"`.
- Populate `IconId` when known.
- Leave `ItemType`, `CanBeHq`, `IsMarketable`, and `StackSize` null for now unless Garland detail provides reliable values.

- [ ] **Step 3: Verify provider tests pass**

Run the focused test command again.

Expected: pass.

## Task 2: Craft Architect Gateway Routes

**Files:**
- Modify: `src/FFXIV Craft Architect.LodestoneLookup/Program.cs`
- Test: `src/FFXIV Craft Architect.Tests/XivDataGatewayEndpointTests.cs`
- Test helper if needed: `src/FFXIV Craft Architect.LodestoneLookup/Program.cs` should expose `public partial class Program`.

- [ ] **Step 1: Add route tests**

Add endpoint-level tests using `WebApplicationFactory<Program>` or a focused route-invocation helper.

Required behavior:

- `GET /xivdata/items/search?q=darksteel` returns `200` with JSON containing `items`.
- `GET /xivdata/items/search?q=` returns `400`.
- `GET /xivdata/items/5057` returns `200` when provider resolves the item.
- `GET /xivdata/items/99999999` returns `404`.
- Provider exceptions return `503` or `502` with `errorCode`.

Run:

```powershell
dotnet test "src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~XivDataGatewayEndpointTests" -v minimal
```

Expected: fail because routes are missing.

- [ ] **Step 2: Register provider and map routes**

Update `Program.cs`:

```csharp
builder.Services.AddHttpClient<GarlandService>();
builder.Services.AddSingleton<IGarlandService>(sp => sp.GetRequiredService<GarlandService>());
builder.Services.AddSingleton<IXivItemDataProvider, GarlandXivItemDataProvider>();

app.MapGet("/xivdata/items/search", async (...) => ...);
app.MapGet("/xivdata/items/{itemId:uint}", async (...) => ...);
```

Route rules:

- Empty or one-character non-numeric query returns `400`.
- Search limit defaults to `20`, clamps to `1..50`.
- Missing item returns `404`.
- Provider `HttpRequestException` returns `503`.
- Unexpected provider data/parse exceptions return `502`.

- [ ] **Step 3: Verify route tests pass**

Run the focused endpoint test command.

Expected: pass.

## Task 3: MarketMafioso Gateway Configuration And Dashboard Validation

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
- Modify: `docs/hosted-receiver.md`

- [ ] **Step 1: Add failing MarketMafioso dashboard tests**

Add tests that render `/acquisition` and assert:

- The page exposes a configured XIV data base URL, defaulting to `/api/xivdata` in hosted dev.
- The item ID field is hidden or readonly rather than a primary manual input.
- The form includes queue-builder hooks.

Add creation tests:

- Browser form with `itemName=Darksteel Nugget` and blank `itemId` returns `400`.
- Browser form with `itemId=5057` and blank `itemName` succeeds but stores display fallback `Item 5057`.

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests" -v minimal -p:UseSharedCompilation=false
```

Expected: fail on missing dashboard UI hooks.

- [ ] **Step 2: Implement configuration and validation**

Update `Program.cs`:

- Read `MarketMafioso:XivDataBaseUrl`.
- Default hosted dashboard value to `${PublicOrigin}/api/xivdata` when `PublicOrigin` is configured.
- Render the value into the acquisition dashboard as `data-xiv-data-base-url`.
- Keep `ReadAcquisitionFormAsync` requiring `itemId > 0`.
- If `itemName` is blank, set stored item name to `Item {itemId}`.

- [ ] **Step 3: Verify MarketMafioso server tests pass**

Run focused tests again.

Expected: pass.

## Task 4: MarketMafioso Acquisition Queue Builder UI

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add failing dashboard markup tests**

Assert the acquisition dashboard contains:

- Search input with `id="acquisitionItemSearch"`.
- Suggestions container with `id="acquisitionItemSuggestions"`.
- Hidden item fields `selectedItemId` and `selectedItemName`.
- Queue table body `id="acquisitionQueueRows"`.
- `Add to Queue` and `Stage Queue` buttons.
- Browser script function names `searchAcquisitionItems`, `addAcquisitionQueueRow`, and `stageAcquisitionQueue`.

Run focused tests and verify failure.

- [ ] **Step 2: Implement dashboard markup and JavaScript**

Update `RenderAcquisitionRequestForm`:

- Replace primary manual `Item ID` field with item search/select UI.
- Keep a small readonly resolved item display: name, item ID, optional type.
- Add queue row controls for quantity, HQ policy, max unit price, gil cap, world mode, and expiry.
- Add JavaScript that:
  - Debounces search.
  - Calls `${xivDataBaseUrl}/items/search?q=...`.
  - Renders clickable suggestions.
  - Stores selected item ID/name in hidden fields.
  - Adds validated queue rows to an in-memory array.
  - Posts one URL-encoded request per queue row to `/acquisition/requests` with the CSRF token.
  - Reports partial success/failure per row.

- [ ] **Step 3: Verify focused tests pass**

Run focused MarketMafioso acquisition endpoint tests.

Expected: pass.

## Task 5: Verification And Deployment Handoff

**Files:**
- Commit Craft Architect worktree changes.
- Commit MarketMafioso changes.

- [ ] **Step 1: Run Craft Architect focused tests**

```powershell
dotnet test "src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj" --filter "FullyQualifiedName~XivData" -v minimal
```

- [ ] **Step 2: Run MarketMafioso focused and full tests**

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests" -v minimal -p:UseSharedCompilation=false
dotnet test "MarketMafioso.sln" -c Debug -v minimal --no-restore -p:UseSharedCompilation=false
dotnet format "MarketMafioso.sln" --verify-no-changes
```

- [ ] **Step 3: Commit and push**

MarketMafioso:

```powershell
git add docs/superpowers/plans/2026-06-25-shared-xiv-data-gateway-implementation.md MarketMafioso.Server MarketMafioso.Server.Tests docs/hosted-receiver.md
git commit -m "Add acquisition item selector gateway consumer"
git push origin local-dev
git branch -f main HEAD
git push origin main
```

Craft Architect isolated worktree:

```powershell
git add "src/FFXIV Craft Architect.LodestoneLookup" "src/FFXIV Craft Architect.Tests"
git commit -m "Add shared XIV item data gateway"
git push origin feature/xiv-data-gateway
```

- [ ] **Step 4: Deployment note**

Do not merge/deploy Craft Architect gateway into `local-dev` until the active dirty Trade work is reconciled or the maintainer approves merging the feature branch. MarketMafioso dashboard can be tested against a local or branch-deployed gateway once available.

## Self-Review

- Spec coverage: covers route shape, DTOs, provider boundary, explicit self-host behavior, browser-safe auth, queue builder, and one-request-per-item pickup compatibility.
- Placeholder scan: no placeholders or deferred unspecified steps.
- Type consistency: `XivItemSummary`, `IXivItemDataProvider`, and route names are consistent across tasks.
