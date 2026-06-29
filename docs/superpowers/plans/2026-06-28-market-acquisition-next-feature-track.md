# Market Acquisition Next Feature Track Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the next Market Acquisition feature track end-to-end: per-line truth, clearer batch UI, sortable inspection tables, opportunistic checks, scoped sweep polish, Universalis freshness verification, and post-run diagnostics.

**Architecture:** Keep the server as the durable source of request/batch lifecycle, line projections, and attempt events. Keep the plugin as the executor of live market-board truth and the producer of route, line, purchase, and freshness events. Keep dashboard and plugin UI as projections over explicit batch, line, route-stop, and attempt state rather than inventing state locally.

**Tech Stack:** C# 12, .NET 8, ASP.NET minimal APIs, SQLite, Dalamud ImGui, Blazor WebAssembly, MudBlazor, xUnit.

**Execution status (2026-06-29):** Source work through Task 16 is implemented and focused tests are passing. The dev plugin was deployed from `main@7339ce2` with visible manifest `1.1.213.30767`; the dev receiver/dashboard was last deployed from `local-dev@c055e4a` with public smoke checks passing. Remaining gates are live gameplay validation of multi-item opportunistic batches and deeper market-board pagination beyond the first readable cache.

---

## Scope And Dependency Order

This plan covers the whole feature list so execution does not drift. The work is still ordered by dependency:

1. Preserve and commit the already-landed scoped all-world sweep slice with its documentation.
2. Add reusable test builders for batch, line, route, and HTTP-client fixtures used throughout this plan.
3. Build authoritative per-line progress and purchase audit, because later UI and summaries need real line truth.
4. Make the claimed batch UI line-aware using the new line projection.
5. Add sortable advisory plan tables as an independent inspection improvement.
6. Add per-world completion summaries on top of line and world stop data.
7. Add opportunistic full-batch checks, default on with a plugin-side setting to disable.
8. Generalize scoped all-world sweep beyond North America where travel scope is supported.
9. Add Universalis freshness verification as loud diagnostics, never route blocking.
10. Add dashboard/log convenience surfaces once route and diagnostic data are stable.

## Files And Responsibilities

- `docs/design/2026-06-28-market-acquisition-next-feature-list.md`
  - Keep feature status and discoveries updated as execution proceeds.
- `docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md`
  - Update Phase 6 and later statuses when per-line progress/audit lands.
- `MarketMafioso.Server/MarketAcquisitionModels.cs`
  - Add line progress, purchase audit, freshness, and world-summary DTOs.
- `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
  - Own SQLite schema migrations, line projection updates, purchase audit inserts, event projection, and validation.
- `MarketMafioso.Server/Program.cs`
  - Expose canonical `/marketmafioso/api/acquisition/batches/...` endpoints.
- `MarketMafioso.Server.Tests/MarketAcquisitionRequestStoreTests.cs`
  - Store-level lifecycle, line, audit, and projection tests.
- `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
  - API contract tests for line progress, purchase audit, freshness, and terminal states.
- `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
  - Shared plugin-side DTOs for line progress/audit/freshness.
- `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
  - Plugin HTTP client methods for line progress, purchase audit, world summaries, and freshness results.
- `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
  - Emit route diagnostics with stable batch, attempt, line, route-stop, and world identifiers.
- `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
  - Track active line/subtask, per-line purchased/spent totals, and world completion state.
- `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveCandidatePlanner.cs`
  - Preserve planned-vs-opportunistic classification and line constraints.
- `MarketMafioso/MarketAcquisition/UniversalisMarketFreshnessVerifier.cs`
  - New service that verifies post-world Universalis freshness.
- `MarketMafioso/Windows/MainWindow.cs`
  - Plugin claimed batch UI, advisory plan sorting, route controls, and settings.
- `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
  - Detailed diagnostic surfaces for route events, input capture, candidate decisions, and freshness.
- `MarketMafioso/Configuration.cs`
  - Persist default-on opportunistic checks and any route diagnostic display settings.
- `MarketMafioso.Tests/MarketAcquisition/*.cs`
  - Planner, route session, route runner, live candidate, client, and freshness tests.
- `MarketMafioso.Dashboard/Components/Acquisition/*.razor`
  - Dashboard line details, attempt history, archive/preset surfaces, and route log index.
- `MarketMafioso.Dashboard/Models/DashboardModels.cs`
  - Dashboard DTOs for line, audit, freshness, and summaries.

---

## Task 0: Preserve Scoped Sweep Slice And Documentation

**Files:**
- Modify: `docs/design/2026-06-28-market-acquisition-next-feature-list.md`
- Modify: `docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md`
- Existing modified code files from scoped sweep slice:
  - `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
  - `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
  - `MarketMafioso.Server/MarketAcquisitionModels.cs`
  - `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
  - `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
  - `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
  - `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
  - `MarketMafioso.Dashboard/Models/DashboardModels.cs`
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs`

- [x] **Step 1: Verify scoped sweep planner tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionPlannerTests" -v minimal
```

Expected: all planner tests pass, including scoped sweep tests for region and selected data centers.

- [x] **Step 2: Verify server acquisition tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
```

Expected: all filtered server acquisition tests pass.

- [x] **Step 3: Update roadmap status for scoped sweep**

In `docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md`, add a short note under Deferred or the relevant phase:

```markdown
- Scoped all-world sweep has a first implementation slice: server and dashboard contracts can select region, current data center, or selected data centers, and the planner can create probe-only world stops. Live validation and diagnostics polish remain in the next feature track.
```

- [x] **Step 4: Commit scoped sweep and documents**

Run:

```powershell
git add "docs/design/2026-06-28-market-acquisition-next-feature-list.md" `
        "docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md" `
        "MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs" `
        "MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs" `
        "MarketMafioso.Server/MarketAcquisitionModels.cs" `
        "MarketMafioso.Server/MarketAcquisitionRequestStore.cs" `
        "MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor" `
        "MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor" `
        "MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor" `
        "MarketMafioso.Dashboard/Models/DashboardModels.cs" `
        "MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs"
git commit -m "feat: add scoped all-world sweep planning"
```

Expected: commit succeeds. If unrelated dirty files are present, do not stage them.

---

## Task 1: Add Shared Market Acquisition Test Builders

**Files:**
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionTestApp.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionStoreFixture.cs`
- Modify: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionTestPlans.cs`
- Modify: `MarketMafioso.Tests/MarketAcquisition/RecordingHttpMessageHandler.cs`
- Modify or create: `MarketMafioso.Tests/TestUtilities/TemporaryDirectory.cs`

- [x] **Step 1: Locate existing helpers**

Run:

```powershell
rg -n "CreateAcceptedBatchAsync|CreateClaimedBatchAsync|RecordingHttpMessageHandler|TemporaryDirectory|TestPlans|MarketAcquisitionTestPlans" "MarketMafioso.Tests" "MarketMafioso.Server.Tests"
```

Expected: identify existing helper names and paths. If a helper already exists with a different name, adapt the later plan steps to that local name instead of creating duplicate scaffolding.

- [x] **Step 2: Add server endpoint helpers**

Add or extend helpers so endpoint tests can do this without duplicating request setup:

```csharp
var claimed = await app.CreateAcceptedBatchAsync(client, "test-slug", lineCount: 2);
var fetched = await app.GetBatchAsync(client, claimed.Id);
```

The helper must:

- create a dashboard request with `lineCount` lines,
- claim or accept it according to the helper name,
- return a typed view containing batch id, claim token, and line ids,
- use unique idempotency keys derived from the supplied slug.

- [x] **Step 3: Add store fixture helpers**

Add or extend store helpers so store tests can create accepted batches directly:

```csharp
var claimed = await store.CreateAcceptedBatchAsync("test-slug", lineCount: 2);
```

The helper must return the same typed view shape used by endpoint tests where practical.

- [x] **Step 4: Add plugin route test plans**

Add or extend a static test-plan helper with:

```csharp
MarketAcquisitionTestPlans.MultiLineSingleWorld()
MarketAcquisitionTestPlans.MultiLineTwoWorlds(bool firstWorldHasOnlyFirstLine = false)
MarketAcquisitionTestPlans.ReadyCandidatePlan(uint quantity, uint gil)
```

The plans must include stable request id, line ids, item ids, world names, data centers, and listing ids so route diagnostics can be asserted exactly.

- [x] **Step 5: Add focused HTTP and temporary directory helpers**

Create or reuse:

```csharp
RecordingHttpMessageHandler
TemporaryDirectory
```

`RecordingHttpMessageHandler` records method, URI, auth header, and request body. `TemporaryDirectory` creates and deletes an isolated directory under the test temp root.

- [x] **Step 6: Run helper compile**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionPlannerTests" -v minimal
dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
```

Expected: existing tests still pass.

---

## Task 2: Add Per-Line Progress And Purchase Audit Contracts

**Files:**
- Modify: `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [x] **Step 1: Add endpoint tests for line progress validation**

Add tests that prove:

```csharp
[Fact]
public async Task LineProgressRejectsUnknownLineId()
{
    using var app = await MarketAcquisitionTestApp.CreateAsync();
    var client = app.CreateAuthenticatedClient();
    var claimed = await app.CreateClaimedBatchAsync(client, "line-progress-unknown");

    var response = await client.PostAsJsonAsync(
        $"/marketmafioso/api/acquisition/batches/{claimed.Id}/lines/not-a-line/progress",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = "line-progress-unknown-key",
            attemptId = "attempt-1",
            sequence = 1,
            status = "Running",
            message = "Testing wrong line."
        });

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task LineProgressUpdatesLineProjection()
{
    using var app = await MarketAcquisitionTestApp.CreateAsync();
    var client = app.CreateAuthenticatedClient();
    var claimed = await app.CreateAcceptedBatchAsync(client, "line-progress-projection", lineCount: 2);
    var line = claimed.Lines[1];

    var response = await client.PostAsJsonAsync(
        $"/marketmafioso/api/acquisition/batches/{claimed.Id}/lines/{line.LineId}/progress",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = "line-progress-projection-key",
            attemptId = "attempt-1",
            sequence = 1,
            status = "Running",
            purchasedQuantity = 5,
            spentGil = 2500,
            message = "Bought one safe stack."
        });

    response.EnsureSuccessStatusCode();
    var view = await client.GetFromJsonAsync<MarketAcquisitionRequestView>(
        $"/marketmafioso/api/acquisition/batches/{claimed.Id}");

    var updatedLine = Assert.Single(view!.Lines.Where(l => l.LineId == line.LineId));
    Assert.Equal("Running", updatedLine.Status);
    Assert.Equal((uint)5, updatedLine.PurchasedQuantity);
    Assert.Equal((uint)2500, updatedLine.SpentGil);
    Assert.Equal("Bought one safe stack.", updatedLine.LatestMessage);
}
```

Expected before implementation: tests fail because the endpoint and helper overloads do not exist.

- [x] **Step 2: Add shared DTOs**

Add these records to both server and plugin DTO model files, keeping namespace-local style:

```csharp
public sealed record MarketAcquisitionLineProgressRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Status { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string LineId { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public uint TotalGil { get; init; }
    public bool IsHq { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? Message { get; init; }
}
```

- [x] **Step 3: Run DTO compile**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build fails only because endpoint/store methods are missing, not because DTO syntax is invalid.

---

## Task 3: Add Server Storage For Line Progress And Purchase Audit

**Files:**
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestStoreTests.cs`

- [x] **Step 1: Add failing store tests**

Add tests proving:

```csharp
[Fact]
public async Task RecordLineProgressAsyncRejectsLineFromDifferentBatch()
{
    using var store = await MarketAcquisitionStoreFixture.CreateAsync();
    var first = await store.CreateAcceptedBatchAsync("wrong-batch-first", lineCount: 1);
    var second = await store.CreateAcceptedBatchAsync("wrong-batch-second", lineCount: 1);

    await Assert.ThrowsAsync<MarketAcquisitionInvalidLineException>(() =>
        store.RecordLineProgressAsync(
            first.Id,
            second.Lines[0].LineId,
            new MarketAcquisitionLineProgressRequest
            {
                ClaimToken = first.ClaimToken,
                IdempotencyKey = "wrong-batch-line-key",
                AttemptId = "attempt-1",
                Sequence = 1,
                Status = "Running",
                Message = "Wrong line."
            },
            CancellationToken.None));
}

[Fact]
public async Task RecordPurchaseAuditAsyncInsertsIdempotentPurchaseRecord()
{
    using var store = await MarketAcquisitionStoreFixture.CreateAsync();
    var claimed = await store.CreateAcceptedBatchAsync("purchase-audit-idempotent", lineCount: 1);
    var request = new MarketAcquisitionPurchaseAuditRequest
    {
        ClaimToken = claimed.ClaimToken,
        IdempotencyKey = "purchase-audit-key",
        AttemptId = "attempt-1",
        Sequence = 1,
        LineId = claimed.Lines[0].LineId,
        WorldName = "Siren",
        ItemId = claimed.Lines[0].ItemId,
        ItemName = claimed.Lines[0].ItemName,
        ListingId = "listing-1",
        RetainerName = "Seller",
        RetainerId = "retainer-1",
        Quantity = 10,
        UnitPrice = 50,
        TotalGil = 500,
        IsHq = false,
        Result = "Purchased",
        Message = "Purchase confirmed."
    };

    var first = await store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);
    var second = await store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);

    Assert.Equal(first.AuditId, second.AuditId);
}
```

Expected before implementation: tests fail because store APIs and exception do not exist.

- [x] **Step 2: Add schema migration**

In the store schema setup, add a purchase audit table:

```sql
CREATE TABLE IF NOT EXISTS acquisition_purchase_audit (
    audit_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL,
    line_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    world_name TEXT NOT NULL,
    item_id INTEGER NOT NULL,
    item_name TEXT NULL,
    listing_id TEXT NOT NULL,
    retainer_name TEXT NOT NULL,
    retainer_id TEXT NOT NULL,
    quantity INTEGER NOT NULL,
    unit_price INTEGER NOT NULL,
    total_gil INTEGER NOT NULL,
    is_hq INTEGER NOT NULL,
    result TEXT NOT NULL,
    message TEXT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE(request_id, attempt_id, sequence),
    FOREIGN KEY(line_id) REFERENCES acquisition_batch_lines(line_id)
);
```

If the store uses migration versioning, add this through the next migration step instead of a standalone `CREATE TABLE` block.

- [x] **Step 3: Implement line validation helper**

Add a helper with this behavior:

```csharp
private static async Task EnsureLineBelongsToRequestAsync(
    SqliteConnection connection,
    SqliteTransaction? transaction,
    string requestId,
    string lineId,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        SELECT COUNT(*)
        FROM acquisition_batch_lines
        WHERE request_id = $requestId AND line_id = $lineId
        """;
    command.Parameters.AddWithValue("$requestId", requestId);
    command.Parameters.AddWithValue("$lineId", lineId);

    var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    if (count == 0)
        throw new MarketAcquisitionInvalidLineException(requestId, lineId);
}
```

- [x] **Step 4: Implement line progress projection**

Implement store method:

```csharp
public async Task<MarketAcquisitionBatchLineView> RecordLineProgressAsync(
    string requestId,
    string lineId,
    MarketAcquisitionLineProgressRequest request,
    CancellationToken cancellationToken)
```

It must:

- validate claim token against the batch,
- validate line belongs to the batch,
- apply idempotency rules using request id, line id, attempt id, sequence, and body hash,
- update `acquisition_batch_lines.status`,
- update `purchased_quantity` to the latest absolute quantity reported,
- update `spent_gil` to the latest absolute gil reported,
- update `latest_message`,
- return the updated line view.

- [x] **Step 5: Implement purchase audit insert**

Implement store method:

```csharp
public async Task<MarketAcquisitionPurchaseAuditView> RecordPurchaseAuditAsync(
    string requestId,
    MarketAcquisitionPurchaseAuditRequest request,
    CancellationToken cancellationToken)
```

It must:

- validate claim token,
- validate `request.LineId` belongs to `requestId`,
- insert exactly one audit row for the idempotent payload,
- return the existing row on exact replay,
- throw idempotency conflict if key/sequence is reused with a different body.

- [x] **Step 6: Run store tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestStore" -v minimal
```

Expected: new store tests pass.

---

## Task 4: Expose Line Progress And Purchase Audit Endpoints

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [x] **Step 1: Add canonical endpoints**

Add:

```csharp
marketMafiosoApi.MapPost(
    "/acquisition/batches/{id}/lines/{lineId}/progress",
    async (
        string id,
        string lineId,
        MarketAcquisitionLineProgressRequest request,
        MarketAcquisitionRequestStore store,
        CancellationToken token) =>
    {
        var line = await store.RecordLineProgressAsync(id, lineId, request, token).ConfigureAwait(false);
        return Results.Ok(line);
    });

marketMafiosoApi.MapPost(
    "/acquisition/batches/{id}/purchases",
    async (
        string id,
        MarketAcquisitionPurchaseAuditRequest request,
        MarketAcquisitionRequestStore store,
        CancellationToken token) =>
    {
        var audit = await store.RecordPurchaseAuditAsync(id, request, token).ConfigureAwait(false);
        return Results.Ok(audit);
    });
```

Wrap the endpoints in the same exception-to-response mapping used by existing acquisition lifecycle endpoints.

- [x] **Step 2: Ensure old namespace stays retired**

Add endpoint tests:

```csharp
[Fact]
public async Task OldApiNamespaceDoesNotExposeLineProgress()
{
    using var app = await MarketAcquisitionTestApp.CreateAsync();
    var client = app.CreateAuthenticatedClient();

    var response = await client.PostAsJsonAsync(
        "/api/marketmafioso/acquisition/batches/request-1/lines/line-1/progress",
        new { });

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
```

- [x] **Step 3: Run endpoint tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests" -v minimal
```

Expected: endpoint tests pass.

---

## Task 5: Add Plugin Client Methods For Line Progress And Audit

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`

- [x] **Step 1: Add failing client tests**

Add tests that assert route and body:

```csharp
[Fact]
public async Task PostLineProgressAsyncUsesCanonicalBatchLineEndpoint()
{
    var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, """
        {"lineId":"line-1","batchId":"batch-1","status":"Running"}
        """);
    var client = new MarketAcquisitionRequestClient(new HttpClient(handler));

    await client.PostLineProgressAsync(
        "https://example.test/marketmafioso/api/acquisition",
        "client-secret",
        "batch-1",
        "line-1",
        new MarketAcquisitionLineProgressRequest
        {
            ClaimToken = "claim-token",
            IdempotencyKey = "key-1",
            AttemptId = "attempt-1",
            Sequence = 1,
            Status = "Running",
            Message = "Line running."
        },
        CancellationToken.None);

    Assert.EndsWith("/batches/batch-1/lines/line-1/progress", handler.RequestUri!.AbsolutePath);
}
```

- [x] **Step 2: Implement client methods**

Add:

```csharp
public Task<MarketAcquisitionBatchLineView> PostLineProgressAsync(
    string serverUrl,
    string clientApiKey,
    string requestId,
    string lineId,
    MarketAcquisitionLineProgressRequest request,
    CancellationToken cancellationToken) =>
    PostLifecycleAsync<MarketAcquisitionBatchLineView>(
        serverUrl,
        clientApiKey,
        requestId,
        $"lines/{Uri.EscapeDataString(lineId)}/progress",
        request,
        cancellationToken);

public Task<MarketAcquisitionPurchaseAuditView> PostPurchaseAuditAsync(
    string serverUrl,
    string clientApiKey,
    string requestId,
    MarketAcquisitionPurchaseAuditRequest request,
    CancellationToken cancellationToken) =>
    PostLifecycleAsync<MarketAcquisitionPurchaseAuditView>(
        serverUrl,
        clientApiKey,
        requestId,
        "purchases",
        request,
        cancellationToken);
```

If `PostLifecycleAsync` cannot address nested paths cleanly, add a small path builder that accepts path segments and reuses the same auth/idempotency behavior.

- [x] **Step 3: Run client tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestClientTests" -v minimal
```

Expected: client tests pass.

---

## Task 6: Teach Route Session Per-Line Totals

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`

- [x] **Step 1: Add failing route-session tests**

Add:

```csharp
[Fact]
public void RecordWorldPurchaseBatchCompleteAccumulatesActiveLineTotals()
{
    var plan = MarketAcquisitionTestPlans.MultiLineSingleWorld();
    var route = MarketAcquisitionGuidedRouteSession.Start(plan);
    route.RecordCurrentWorld("Siren");
    route.RecordProbe("Siren", MarketAcquisitionTestPlans.ReadyCandidatePlan(quantity: 10, gil: 500));

    route.RecordWorldPurchaseBatchComplete("Siren", purchasedQuantity: 10, spentGil: 500);

    var stop = Assert.Single(route.Stops);
    var firstLine = Assert.Single(stop.LineStates.Where(line => line.LineId == plan.Lines[0].LineId));
    Assert.Equal((uint)10, firstLine.PurchasedQuantity);
    Assert.Equal((uint)500, firstLine.SpentGil);
    Assert.Equal("Complete", firstLine.Status);
}
```

Expected before implementation: test fails because line states do not exist.

- [x] **Step 2: Add route stop line state model**

Add:

```csharp
public sealed record MarketAcquisitionRouteLineState
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string Status { get; set; } = "Pending";
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public string? LatestMessage { get; set; }
}
```

Add `IReadOnlyList<MarketAcquisitionRouteLineState> LineStates` to `MarketAcquisitionGuidedRouteStop`.

- [x] **Step 3: Initialize line states from item subtasks**

During `Start(plan)`, create one line state per item subtask on each world:

```csharp
LineStates = batch.ItemSubtasks
    .Select(subtask => new MarketAcquisitionRouteLineState
    {
        LineId = subtask.LineId,
        ItemId = subtask.ItemId,
        ItemName = subtask.ItemName,
        PlannedQuantity = subtask.PlannedQuantity,
        PlannedGil = subtask.PlannedGil,
    })
    .ToList()
```

- [x] **Step 4: Update active line state on probe and purchase completion**

When a probe yields no candidates:

```csharp
var activeLine = stop.ActiveLineState;
if (activeLine != null)
{
    activeLine.Status = "SkippedNoLiveStock";
    activeLine.LatestMessage = candidatePlan.Message;
}
```

When a purchase batch completes:

```csharp
var activeLine = stop.ActiveLineState;
if (activeLine != null)
{
    activeLine.PurchasedQuantity = checked(activeLine.PurchasedQuantity + purchasedQuantity);
    activeLine.SpentGil = checked(activeLine.SpentGil + spentGil);
    activeLine.Status = purchasedQuantity > 0 ? "Complete" : "SkippedNoLiveStock";
    activeLine.LatestMessage = $"Purchased {purchasedQuantity:N0}, spent {spentGil:N0} gil.";
}
```

- [x] **Step 5: Run route-session tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionGuidedRouteSessionTests" -v minimal
```

Expected: route session line-state tests pass.

---

## Task 7: Emit Line Progress And Purchase Audit From Plugin Route

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`

- [x] **Step 1: Add route diagnostics expectation**

Add route runner test proving line id appears in diagnostics:

```csharp
[Fact]
public void RecordLineProgressWritesLineIdentity()
{
    using var temp = new TemporaryDirectory();
    var runner = new MarketAcquisitionRouteRunner(temp.Path);
    runner.Start(MarketAcquisitionTestPlans.MultiLineSingleWorld());

    runner.RecordLineProgress(
        lineId: "batch-1-line-2",
        itemName: "Silver Ingot",
        status: "Running",
        purchasedQuantity: 5,
        spentGil: 2500,
        message: "Bought safe listing.");

    var log = File.ReadAllText(runner.LastDiagnosticFilePath!);
    Assert.Contains("lineId: batch-1-line-2", log, StringComparison.Ordinal);
    Assert.Contains("Silver Ingot", log, StringComparison.Ordinal);
}
```

- [x] **Step 2: Add plugin line progress helper**

In `MainWindow`, add:

```csharp
private Task ReportAcquisitionLineProgressAsync(
    MarketAcquisitionWorldItemSubtask subtask,
    string status,
    uint purchasedQuantity,
    uint spentGil,
    string message,
    CancellationToken token)
{
    var claimed = claimedAcquisitionRequest ??
                  throw new InvalidOperationException("No dashboard request is claimed.");

    return acquisitionClient.PostLineProgressAsync(
        urlBuffer,
        apiKeyBuffer,
        claimed.Id,
        subtask.LineId,
        new MarketAcquisitionLineProgressRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = $"{guidedRouteProgressNonce}:{guidedRouteProgressReportSequence}:line:{subtask.LineId}",
            AttemptId = guidedRouteProgressNonce,
            Sequence = Interlocked.Increment(ref guidedRouteProgressReportSequence),
            Status = status,
            PurchasedQuantity = purchasedQuantity,
            SpentGil = spentGil,
            Message = message,
        },
        token);
}
```

Use the existing progress nonce/sequence model. If sequence must remain single-use across event kinds, reserve a helper that increments exactly once per outbound event.

- [x] **Step 3: Add plugin purchase audit helper**

In `MainWindow`, add:

```csharp
private Task ReportAcquisitionPurchaseAuditAsync(
    MarketAcquisitionWorldItemSubtask subtask,
    MarketBoardPurchaseCandidate candidate,
    string result,
    string message,
    CancellationToken token)
{
    var claimed = claimedAcquisitionRequest ??
                  throw new InvalidOperationException("No dashboard request is claimed.");

    return acquisitionClient.PostPurchaseAuditAsync(
        urlBuffer,
        apiKeyBuffer,
        claimed.Id,
        new MarketAcquisitionPurchaseAuditRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = $"{guidedRouteProgressNonce}:{guidedRouteProgressReportSequence}:purchase:{candidate.ListingId}",
            AttemptId = guidedRouteProgressNonce,
            Sequence = Interlocked.Increment(ref guidedRouteProgressReportSequence),
            LineId = subtask.LineId,
            WorldName = subtask.WorldName,
            ItemId = subtask.ItemId,
            ItemName = subtask.ItemName,
            ListingId = candidate.ListingId,
            RetainerName = candidate.RetainerName,
            RetainerId = candidate.RetainerId,
            Quantity = candidate.Quantity,
            UnitPrice = candidate.UnitPrice,
            TotalGil = candidate.TotalGil,
            IsHq = candidate.IsHq,
            Result = result,
            Message = message,
        },
        token);
}
```

Adjust property names to match `MarketBoardPurchaseCandidate`; keep the payload complete and stable.

- [x] **Step 4: Call line progress at subtask boundaries**

Emit:

- `Running` when a line search begins.
- `SkippedNoLiveStock` when live candidate plan has no safe candidates.
- `Complete` when the route advances after purchases.
- `Failed` only for a line-specific failure that does not stop the batch.

Batch-level route failures continue using existing lifecycle progress/fail endpoints.

- [x] **Step 5: Run plugin build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: plugin builds.

---

## Task 8: Claimed Batch Line-Aware Plugin UI

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: focused build only unless UI helper tests already exist.

- [x] **Step 1: Split claimed UI into batch summary and line table**

Replace the existing single key-value claimed table with:

```csharp
private void DrawClaimedAcquisitionRequest()
{
    ImGuiUi.SectionHeader("Claimed Batch", ColHeader);

    if (claimedAcquisitionRequest == null)
    {
        ImGui.TextColored(ColMuted, "No batch is claimed by this plugin session.");
        return;
    }

    DrawClaimedBatchSummary(claimedAcquisitionRequest);
    ImGui.Spacing();
    DrawClaimedBatchLines(claimedAcquisitionRequest);
    ImGui.Spacing();
    DrawClaimedBatchActions(claimedAcquisitionRequest);
}
```

- [x] **Step 2: Draw true batch-level summary**

Add:

```csharp
private static void DrawClaimedBatchSummary(MarketAcquisitionClaimView claimed)
{
    if (!ImGui.BeginTable("MarketAcquisitionClaimedBatchSummary", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        return;

    DrawClaimedRequestRow("Status", claimed.Status);
    DrawClaimedRequestRow("Target", $"{claimed.TargetCharacterName} @ {claimed.TargetWorld}");
    DrawClaimedRequestRow("Lines", FormatAcquisitionLineCount(claimed));
    DrawClaimedRequestRow("Routing", FormatClaimedBatchRouting(claimed));
    DrawClaimedRequestRow("Latest", string.IsNullOrWhiteSpace(claimed.LatestMessage) ? "-" : claimed.LatestMessage);
    ImGui.EndTable();
}
```

- [x] **Step 3: Draw line table**

Add:

```csharp
private static void DrawClaimedBatchLines(MarketAcquisitionClaimView claimed)
{
    const ImGuiTableFlags flags =
        ImGuiTableFlags.Borders |
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Resizable |
        ImGuiTableFlags.ScrollX;

    if (!ImGui.BeginTable("MarketAcquisitionClaimedBatchLines", 9, flags))
        return;

    ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
    ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 120);
    ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 88);
    ImGui.TableSetupColumn("Max Qty", ImGuiTableColumnFlags.WidthFixed, 88);
    ImGui.TableSetupColumn("Gil Cap", ImGuiTableColumnFlags.WidthFixed, 88);
    ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
    ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 88);
    ImGui.TableSetupColumn("Spent", ImGuiTableColumnFlags.WidthFixed, 88);
    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 128);
    ImGui.TableHeadersRow();

    foreach (var line in claimed.Lines.OrderBy(line => line.Ordinal))
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatLineItem(line));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatLineMaxQuantity(line));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatGilCap(line.GilCap));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.HqPolicy);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.PurchasedQuantity == 0 ? "-" : line.PurchasedQuantity.ToString("N0"));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.SpentGil == 0 ? "-" : FormatGil(line.SpentGil));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(line.Status) ? "-" : line.Status);
    }

    ImGui.EndTable();
}
```

- [x] **Step 4: Keep actions batch-level**

Move existing accept/reject/forget/prepare buttons into `DrawClaimedBatchActions`.

- [x] **Step 5: Build plugin**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: plugin builds.

---

## Task 9: Sortable Advisory Plan Tables

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: focused build plus live visual check.

- [x] **Step 1: Add advisory table sort state**

Add fields:

```csharp
private int advisoryPlanSortColumn;
private ImGuiSortDirection advisoryPlanSortDirection = ImGuiSortDirection.Ascending;
```

- [x] **Step 2: Enable sort flags**

In `DrawMarketAcquisitionPlan`, change the world listings table flags to include sorting:

```csharp
const ImGuiTableFlags tableFlags =
    ImGuiTableFlags.Borders |
    ImGuiTableFlags.RowBg |
    ImGuiTableFlags.Resizable |
    ImGuiTableFlags.ScrollX |
    ImGuiTableFlags.Sortable |
    ImGuiTableFlags.SortMulti;
```

Set sortable columns:

```csharp
ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 120);
ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 96);
ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 80);
ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 48);
ImGui.TableSetupColumn("Listing", ImGuiTableColumnFlags.WidthStretch);
```

- [x] **Step 3: Sort display rows only**

Build a row projection:

```csharp
private sealed record AdvisoryPlanRow(
    int RouteOrdinal,
    string Item,
    string World,
    string DataCenter,
    uint Quantity,
    uint Gil,
    uint Unit,
    bool IsHq,
    string Listing);
```

Sort only the projection:

```csharp
private static IEnumerable<AdvisoryPlanRow> SortAdvisoryPlanRows(
    IReadOnlyList<AdvisoryPlanRow> rows,
    ImGuiTableSortSpecsPtr sortSpecs)
{
    if (sortSpecs.SpecsCount == 0)
        return rows.OrderBy(row => row.RouteOrdinal);

    var spec = sortSpecs.Specs;
    return spec.ColumnIndex switch
    {
        0 => SortBy(rows, row => row.Item, spec.SortDirection),
        1 => SortBy(rows, row => row.World, spec.SortDirection),
        2 => SortBy(rows, row => row.DataCenter, spec.SortDirection),
        3 => SortBy(rows, row => row.Quantity, spec.SortDirection),
        4 => SortBy(rows, row => row.Gil, spec.SortDirection),
        5 => SortBy(rows, row => row.Unit, spec.SortDirection),
        6 => SortBy(rows, row => row.IsHq, spec.SortDirection),
        _ => rows.OrderBy(row => row.RouteOrdinal),
    };
}
```

Do not reorder `acquisitionPlan.WorldBatches`.

- [x] **Step 4: Build plugin**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build passes.

---

## Task 10: Per-World Completion Summary

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`

- [x] **Step 1: Add world summary model**

Add:

```csharp
public sealed record MarketAcquisitionWorldCompletionSummary
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public int CompletedLineCount { get; init; }
    public int SkippedLineCount { get; init; }
    public int FailedLineCount { get; init; }
    public string Message { get; init; } = string.Empty;
}
```

- [x] **Step 2: Build summary when completing a stop**

In route session:

```csharp
private static MarketAcquisitionWorldCompletionSummary BuildWorldSummary(MarketAcquisitionGuidedRouteStop stop) => new()
{
    WorldName = stop.WorldName,
    DataCenter = stop.DataCenter,
    PurchasedQuantity = stop.PurchasedQuantity,
    SpentGil = stop.SpentGil,
    CompletedLineCount = stop.LineStates.Count(line => line.Status == "Complete"),
    SkippedLineCount = stop.LineStates.Count(line => line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)),
    FailedLineCount = stop.LineStates.Count(line => line.Status == "Failed"),
    Message = $"{stop.WorldName} complete: bought {stop.PurchasedQuantity:N0} item(s), spent {stop.SpentGil:N0} gil across {stop.LineStates.Count:N0} line(s).",
};
```

- [x] **Step 3: Write summary to diagnostics**

Add `RecordWorldSummary(MarketAcquisitionWorldCompletionSummary summary)` in route runner and write:

```text
World complete
  world: Maduin
  dataCenter: Dynamis
  purchasedQuantity: 612
  spentGil: 318400
  completedLineCount: 3
  skippedLineCount: 1
```

- [x] **Step 4: Show latest summary in plugin UI**

Add a compact latest-world summary below route status. Keep full details in diagnostics.

- [x] **Step 5: Run route runner tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteRunnerTests" -v minimal
```

Expected: tests pass.

Actual: route runner tests passed, and `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug` passed.

---

## Task 11: Opportunistic Full-Batch World Checks

**Files:**
- Modify: `MarketMafioso/Configuration.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs`

- [x] **Step 1: Add default-on setting**

In `Configuration.cs`, add:

```csharp
public bool EnableOpportunisticWorldChecks { get; set; } = true;
```

In Settings or Market Acquisition UI, add:

```csharp
var enableOpportunistic = config.EnableOpportunisticWorldChecks;
if (ImGui.Checkbox("Check all batch items on each visited world", ref enableOpportunistic))
{
    config.EnableOpportunisticWorldChecks = enableOpportunistic;
    config.Save();
}
```

- [x] **Step 2: Add route-session test**

Add a test proving a world stop gets planned subtasks first, then opportunistic subtasks for unfinished lines:

```csharp
[Fact]
public void StartWithOpportunisticChecksAddsUnplannedLinesToWorldStop()
{
    var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds(firstWorldHasOnlyFirstLine: true);

    var route = MarketAcquisitionGuidedRouteSession.Start(plan, includeOpportunisticChecks: true);

    var firstStop = route.Stops[0];
    Assert.Contains(firstStop.ItemSubtasks, subtask => subtask.LineId == plan.Lines[0].LineId && subtask.Source == "Planned");
    Assert.Contains(firstStop.ItemSubtasks, subtask => subtask.LineId == plan.Lines[1].LineId && subtask.Source == "Opportunistic");
}
```

Expected before implementation: compile fails because `Source` and overload do not exist.

- [x] **Step 3: Add subtask source**

In `MarketAcquisitionWorldItemSubtask`, add:

```csharp
public string Source { get; init; } = "Planned";
```

Planner-created subtasks use `"Planned"` for ordinary recommendations and `"SweepProbe"` for scope-probe stops created by all-world sweep.

- [x] **Step 4: Build opportunistic subtasks at route-session start**

When `includeOpportunisticChecks` is true, for each world stop append one zero-listing subtask for each unfinished plan line not already present on that world:

```csharp
private static IReadOnlyList<MarketAcquisitionWorldItemSubtask> AddOpportunisticSubtasks(
    MarketAcquisitionWorldBatch batch,
    IReadOnlyList<MarketAcquisitionPlanLine> lines)
{
    var existing = batch.ItemSubtasks.Select(subtask => subtask.LineId).ToHashSet(StringComparer.Ordinal);
    var subtasks = batch.ItemSubtasks.ToList();

    foreach (var line in lines.OrderBy(line => line.Ordinal))
    {
        if (existing.Contains(line.LineId))
            continue;

        subtasks.Add(new MarketAcquisitionWorldItemSubtask
        {
            LineId = line.LineId,
            LineOrdinal = line.Ordinal,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            WorldName = batch.WorldName,
            DataCenter = batch.DataCenter,
            QuantityMode = line.QuantityMode,
            RequestedQuantity = line.RequestedQuantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
            Source = "Opportunistic",
        });
    }

    return subtasks;
}
```

- [x] **Step 5: Diagnostics distinguish source**

Log `subtask.Source` when searching, reading, buying, skipping, or completing a subtask.

- [x] **Step 6: Run tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionGuidedRouteSessionTests|FullyQualifiedName~MarketAcquisitionPlannerTests" -v minimal
```

Expected: tests pass.

Actual: focused route-session/planner tests passed, and `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug` passed.

---

## Task 12: Generalize Sweep Region Support

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
- Modify: `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs`

- [x] **Step 1: Add region catalog**

Create or extend a catalog:

```csharp
public static class MarketAcquisitionWorldCatalog
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Regions =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["North America"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aether"] = ["Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren"],
                ["Primal"] = ["Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros"],
                ["Crystal"] = ["Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera"],
                ["Dynamis"] = ["Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"],
            },
            ["Europe"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chaos"] = ["Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan"],
                ["Light"] = ["Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark"],
            },
            ["Japan"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Elemental"] = ["Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon"],
                ["Gaia"] = ["Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima"],
                ["Mana"] = ["Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan"],
                ["Meteor"] = ["Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus"],
            },
            ["Oceania"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Materia"] = ["Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"],
            },
        };
}
```

Verified world names against the current Lodestone world status list before committing this task.

- [x] **Step 2: Replace North-America-only sweep resolver**

Change resolver shape:

```csharp
private static IReadOnlyList<string> ResolveSweepWorlds(MarketAcquisitionRequestView request, string? currentWorld)
{
    var regionCatalog = MarketAcquisitionWorldCatalog.Regions.TryGetValue(request.Region, out var dataCenters)
        ? dataCenters
        : throw new InvalidOperationException($"Region {request.Region} is not supported for all-world sweep.");

    return request.SweepScope switch
    {
        "Region" => dataCenters.Values.SelectMany(worlds => worlds).ToArray(),
        "CurrentDataCenter" => ResolveCurrentDataCenterWorlds(dataCenters, currentWorld ?? request.TargetWorld),
        "DataCenters" => ResolveSelectedDataCenterWorlds(dataCenters, request.SweepDataCenters),
        _ => throw new InvalidOperationException($"Unknown all-world sweep scope {request.SweepScope}."),
    };
}
```

- [x] **Step 3: Dashboard region choices**

Allow region selector to include supported regions. If non-NA route execution is not verified, annotate non-native regions in UI copy as advanced. Do not make Oceania part of a North America default.

- [x] **Step 4: Add planner tests for Europe/Oceania**

Add tests:

```csharp
[Fact]
public void BuildPlan_AllWorldSweepCanResolveOceaniaRegion()
{
    var request = TestRequests.AllWorldSweep(region: "Oceania", sweepScope: "Region");
    var plan = MarketAcquisitionPlanner.BuildPlan(request, [], currentWorld: "Ravana");
    Assert.Contains(plan.WorldBatches, batch => batch.WorldName == "Ravana");
    Assert.Contains(plan.WorldBatches, batch => batch.WorldName == "Sophia");
}
```

- [x] **Step 5: Run planner tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionPlannerTests" -v minimal
```

Expected: tests pass.

Actual: focused planner tests passed 20/20. Focused server settings/store tests passed 7/7. Dashboard project build passed with 0 warnings and 0 errors.

---

## Task 13: Universalis Freshness Verifier

**Files:**
- Create: `MarketMafioso/MarketAcquisition/UniversalisMarketFreshnessVerifier.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/UniversalisMarketFreshnessVerifierTests.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [x] **Step 1: Add freshness tests**

Add:

```csharp
[Fact]
public async Task VerifyAsyncConfirmsWhenLastUploadIsAfterObservation()
{
    var observation = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    using var http = new HttpClient(new StubJsonHandler("""
        {"lastUploadTime":1782648060,"listings":[]}
        """));
    var verifier = new UniversalisMarketFreshnessVerifier(http, new Uri("https://example.test/api/v2/"));

    var result = await verifier.VerifyAsync(
        "Siren",
        itemId: 5064,
        observedAtUtc: observation,
        purchasedListingIds: [],
        CancellationToken.None);

    Assert.Equal("Confirmed", result.Status);
}

[Fact]
public async Task VerifyAsyncReturnsUnconfirmedWhenListingStillPresent()
{
    var observation = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    using var http = new HttpClient(new StubJsonHandler("""
        {"lastUploadTime":1782640000,"listings":[{"listingID":"listing-1"}]}
        """));
    var verifier = new UniversalisMarketFreshnessVerifier(http, new Uri("https://example.test/api/v2/"));

    var result = await verifier.VerifyAsync(
        "Siren",
        itemId: 5064,
        observedAtUtc: observation,
        purchasedListingIds: ["listing-1"],
        CancellationToken.None);

    Assert.Equal("Unconfirmed", result.Status);
}
```

- [x] **Step 2: Implement verifier**

Create:

```csharp
public sealed class UniversalisMarketFreshnessVerifier
{
    private readonly HttpClient httpClient;
    private readonly Uri baseUri;

    public UniversalisMarketFreshnessVerifier(HttpClient httpClient)
        : this(httpClient, new Uri("https://universalis.app/api/v2/"))
    {
    }

    public UniversalisMarketFreshnessVerifier(HttpClient httpClient, Uri baseUri)
    {
        this.httpClient = httpClient;
        this.baseUri = baseUri;
    }

    public async Task<UniversalisFreshnessResult> VerifyAsync(
        string worldName,
        uint itemId,
        DateTimeOffset observedAtUtc,
        IReadOnlyCollection<string> purchasedListingIds,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(baseUri, $"{Uri.EscapeDataString(worldName)}/{itemId}?listings=100");
        using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return UniversalisFreshnessResult.Unavailable($"HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var lastUploadTime = ReadOptionalUnixTime(json.RootElement, "lastUploadTime");
        if (lastUploadTime != null && lastUploadTime >= observedAtUtc)
            return UniversalisFreshnessResult.Confirmed("lastUploadTime is after local observation.");

        if (purchasedListingIds.Count > 0 && !ResponseContainsAnyListing(json.RootElement, purchasedListingIds))
            return UniversalisFreshnessResult.Confirmed("Purchased listings no longer appear in current listings.");

        return UniversalisFreshnessResult.Unconfirmed("Universalis did not reflect the local observation yet.");
    }
}
```

- [x] **Step 3: Run freshness verification at end of each world stop**

After world completion summary is recorded, run verifier once per touched `world + itemId`. Log `Confirmed`, `Unconfirmed`, or `Unavailable`. Do not block route progress.

- [x] **Step 4: Run freshness tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~UniversalisMarketFreshnessVerifierTests" -v minimal
```

Expected: tests pass.

Actual: verifier tests passed 2/2. Focused route-runner plus verifier tests passed 33/33. Freshness checks are diagnostic-only, keyed by purchased listing ids per completed world/item, and use the existing plugin acquisition `HttpClient`.

---

## Task 14: Loud Post-Run Diagnostics For Freshness Failures

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs`
- Modify: `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [x] **Step 1: Add diagnostics summary model**

Add:

```csharp
public sealed record MarketAcquisitionRunDiagnosticSummary
{
    public int FreshnessConfirmedCount { get; init; }
    public int FreshnessUnconfirmedCount { get; init; }
    public int FreshnessUnavailableCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
```

- [x] **Step 2: Record warnings**

When freshness returns `Unconfirmed` or `Unavailable`, add a warning:

```text
Universalis freshness unconfirmed for Silver Ingot on Siren after local market-board observation.
```

- [x] **Step 3: Show post-run warning**

In route UI, after completion:

```csharp
if (lastRunDiagnosticSummary?.Warnings.Count > 0)
{
    ImGui.TextColored(ColError, $"Post-run diagnostics: {lastRunDiagnosticSummary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
}
```

- [x] **Step 4: Build plugin**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build passes.

Actual: focused route-runner freshness tests pass. `MarketAcquisitionRouteRunner` records confirmed, unconfirmed, and unavailable freshness counts; unconfirmed/unavailable checks emit route diagnostics and a post-run warning count in the guided route UI.

---

## Task 15: Plan Explainability And Route Optimizer Transparency

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs`

- [x] **Step 1: Add listing decision model**

Add:

```csharp
public sealed record MarketAcquisitionListingDecision
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
}
```

Add `IReadOnlyList<MarketAcquisitionListingDecision> ListingDecisions` to `MarketAcquisitionPlanDiagnostics`.

- [x] **Step 2: Record explicit reasons**

Planner should emit decisions such as:

- `AcceptedRemoteCandidate`
- `RejectedWrongItem`
- `RejectedAboveMaxUnit`
- `RejectedHqPolicy`
- `RejectedWrongWorldScope`
- `RejectedGilCap`
- `RejectedMaxQuantity`
- `SweepProbeNoRemoteListing`
- `AcceptedRemoteCandidateNotPlanned`

- [x] **Step 3: Add planner test for rejected explanation**

```csharp
[Fact]
public void BuildPlan_DiagnosticsExplainAboveThresholdListings()
{
    var request = TestRequests.SingleLine(maxUnitPrice: 600);
    var listings = new[]
    {
        TestListings.Listing(unitPrice: 700, quantity: 10),
    };

    var plan = MarketAcquisitionPlanner.BuildPlan(request, listings, currentWorld: "Siren");

    Assert.Contains(
        plan.Diagnostics.ListingDecisions,
        decision => decision.Decision == "RejectedAboveMaxUnit" && decision.UnitPrice == 700);
}
```

- [x] **Step 4: Show compact explanation in plugin diagnostics**

In diagnostics window, add a collapsible `Plan Decisions` table with:

`Item`, `World`, `Unit`, `Qty`, `Decision`, `Reason`.

- [x] **Step 5: Run planner tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionPlannerTests" -v minimal
```

Expected: tests pass.

Actual: focused planner tests pass. Plan diagnostics now explain hard-filter rejections, sweep probe worlds, and post-selection cap rejections for gil cap and quantity cap. The diagnostics window includes a collapsible `Plan Decisions` table.

---

## Task 16: Dashboard Route Log Index And Completed Batch Archive

**Files:**
- Modify: `MarketMafioso.Dashboard/Pages/Settings.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`
- Modify: `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`

- [x] **Step 1: Add dashboard API for terminal batches**

Expose a query parameter on existing batch list:

```text
GET /marketmafioso/api/acquisition/batches?includeTerminal=true
```

Server should include terminal rows when `includeTerminal=true`; default remains active/non-terminal rows for the main board.

- [x] **Step 2: Add dashboard archive view**

Add a dashboard tab or filter that shows:

- completed,
- partial/under-procured,
- failed,
- cancelled.

Rows include item count, target, status, latest message, completed time, and actions.

- [x] **Step 3: Add `Run again` payload reuse**

On a terminal batch row, add `Run again` action that rehydrates the request builder with the old lines, routing, sweep scope, HQ policy, max unit, max quantity, and gil cap. It does not immediately stage.

- [x] **Step 4: Add client-local route log notice**

Because route logs are currently plugin-local, add a dashboard diagnostics page section that clearly says:

```text
Route logs are currently stored by the plugin on the client machine. Server-side log indexing requires plugin upload of sanitized route summaries.
```

Do not fake server log visibility.

- [x] **Step 5: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: dashboard builds.

---

## Task 17: Final Verification And Deployment

**Files:**
- No source files expected unless verification exposes defects.

- [x] **Step 1: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests\MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
dotnet test "MarketMafioso.Server.Tests\MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
```

Expected: all filtered tests pass.

- [x] **Step 2: Build solution**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build passes with no errors.

- [x] **Step 3: Deploy plugin for live testing**

Run:

```powershell
& "MarketMafioso\tools\Deploy-DevPlugin.ps1"
```

Expected:

- script reports source and target DLL hashes,
- target is the dedicated deployed DLL path,
- manifest version is visible and incremented.

- [x] **Step 4: Deploy server/dashboard only from the intended branch**

Before VPS deployment:

```powershell
git branch --show-current
git status --short
```

If the active branch is not the deployment branch, merge or cherry-pick intentionally before running the server deploy helper. Do not deploy a branch that does not contain the feature-track commits.

- [x] **Step 5: Update docs with execution discoveries**

Update:

- `docs/design/2026-06-28-market-acquisition-next-feature-list.md`
- `docs/design/2026-06-28-market-acquisition-multi-item-roadmap.md`

Record discoveries about:

- line progress gaps,
- Universalis freshness response reliability,
- opportunistic-check time cost,
- sweep live validation results,
- any new UI automation failure surfaces.

- [x] **Step 6: Commit completed feature track**

Run:

```powershell
git add "MarketMafioso" "MarketMafioso.Server" "MarketMafioso.Dashboard" "MarketMafioso.Tests" "MarketMafioso.Server.Tests" "docs"
git commit -m "feat: harden market acquisition feature track"
```

Expected: commit succeeds after verification.

---

## Plan Self-Review

- Spec coverage:
  - Universalis freshness verifier: Task 13 and Task 14.
  - Per-world completion summary: Task 10.
  - Opportunistic full-batch world checks: Task 11.
  - Scoped all-world sweep: Task 0 and Task 12.
  - Claimed batch line-aware UI: Task 8.
  - Sortable advisory plan table: Task 9.
  - Plan explainability and route transparency: Task 15.
  - Archive/run-again/log convenience: Task 16.
- Dependency check:
  - Per-line progress/audit is implemented before UI claims authoritative per-line purchased/spent state.
  - Opportunistic checks happen after multi-item subtask execution state exists.
  - Universalis verification is diagnostic-only and runs after world completion.
  - Table sorting is inspection-only and does not mutate route order.
- Ambiguity check:
  - Opportunistic checks are default on and configurable off.
  - Universalis failure is loud in post-run diagnostics but never blocks route progress.
  - Sweep should support multiple regions through an explicit region/world catalog, with non-native regions treated as advanced usage rather than the default.

## 2026-06-29 Checkpoint: Visible Listing Cache Exhaustion

Current implementation work distinguishes ordinary no-safe-stock from the market-board visible listing cache being exhausted. The reader can prove when the game reports more listings than the fixed readable cache exposes; the route runner should now preserve that as `SkippedVisibleCacheExhausted` instead of flattening it into `SkippedNoLiveStock`.

This is not deeper pagination. True deeper pagination remains deferred until the plugin has a proven `InfoProxyPageInterface` / market-board request contract or captured packet path for moving beyond the visible listing cache without blindly poking game internals.
