# Market Acquisition Run Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Market Acquisition requests the durable user-facing identity while giving each plugin execution attempt its own attempt id, event sequence, idempotency scope, server diagnostics, and stale-event handling.

**Architecture:** Add explicit acquisition attempt/event contracts to the server and plugin without replacing the existing request queue. The server remains authoritative for request lifecycle and claim validation; the plugin becomes authoritative for current attempt execution and emits typed attempt events with deterministic idempotency keys.

**Tech Stack:** C# 12, ASP.NET Core minimal APIs, SQLite via `Microsoft.Data.Sqlite`, Dalamud plugin `net8.0-windows`, xUnit tests.

**Implementation status (2026-06-27):** Server attempt/event DTOs, SQLite attempt tables, attempt idempotency checks, stale-attempt classification, legacy progress compatibility, plugin attempt DTOs, plugin attempt progress/complete/fail client calls, route-progress attempt emission, and basic dashboard latest-attempt projection are implemented. Remaining follow-up work is a fuller dashboard attempt/event timeline, richer archival/preset surfaces for completed or failed requests, and any additional plugin-side UI suppression needed after live testing.

---

## Reference Documents

- Design source: `docs/design/2026-06-27-market-acquisition-run-model.md`
- Existing lifecycle store: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Existing server contracts: `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Existing plugin client: `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- Existing progress helper: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteProgressReporter.cs`
- Current route UI/reporting coordinator: `MarketMafioso/Windows/MainWindow.cs`

## File Structure

- Create `MarketMafioso.Server/MarketAcquisitionAttemptModels.cs`
  - Server request/response DTOs for attempt events, attempt projections, and structured lifecycle results.
- Modify `MarketMafioso.Server/MarketAcquisitionModels.cs`
  - Add request view fields for latest attempt id, sequence, event type, phase, stale/superseded reason, and plugin version.
- Modify `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
  - Add SQLite tables for `acquisition_request_attempts` and `acquisition_attempt_events`.
  - Add methods to start/update attempts and append idempotent attempt events.
  - Keep existing `acquisition_request_events` path working during migration.
- Modify `MarketMafioso.Server/Program.cs`
  - Add or adapt lifecycle endpoints so progress/complete/fail accept attempt fields.
  - Return structured conflict/stale responses rather than generic 409 strings where possible.
- Modify `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
  - Add server-level tests for attempt idempotency, stale attempt events, terminal late events, and queue projection.
- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionAttemptModels.cs`
  - Plugin-side DTOs mirroring the server attempt event contract.
- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteProgressReporter.cs`
  - Generate attempt-aware idempotency keys and sequence events.
  - Classify structured lifecycle responses.
- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
  - Send attempt id, event sequence, event type, phase, plugin version, route stop id, and world on lifecycle posts.
  - Preserve structured stale/conflict details.
- Modify `MarketMafioso/Windows/MainWindow.cs`
  - Create a new attempt id when starting or restarting a route.
  - Reset sequence per attempt.
  - Suppress irrelevant route/progress warnings after known stale/superseded responses.
  - Keep request pickup hidden while an attempt is active.
- Modify focused plugin tests:
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteProgressReporterTests.cs`
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs` only if route state names change.
- Modify docs:
  - `docs/design/2026-06-27-market-acquisition-run-model.md` only if implementation discovers a needed naming correction.
  - `docs/design/2026-06-25-market-acquisition-roadmap.md` to record the lifecycle model progress after implementation.

## Task 1: Add Server Attempt/Event Contracts

**Files:**
- Create: `MarketMafioso.Server/MarketAcquisitionAttemptModels.cs`
- Modify: `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Test: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add a failing DTO/projection test**

Add this test near the existing acquisition endpoint tests. It can target JSON shape through the real endpoint once Task 2 lands; for this task, leave it skipped only if compilation blocks without the new DTO.

```csharp
[Fact]
public async Task RecentQueueIncludesLatestAttemptProjection()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAndClaimAsync(client, "attempt-projection");

    await SendWithKeyAsync(
        client,
        HttpMethod.Post,
        $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
        "client-secret",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = "attempt-projection-accept",
        });

    var progress = await SendWithKeyAsync(
        client,
        HttpMethod.Post,
        $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
        "client-secret",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = "attempt-projection-progress-1",
            pluginInstanceId = "plugin-test-instance",
            attemptId = "attempt-001",
            eventSequence = 1,
            eventType = "progress",
            phase = "Traveling",
            routeStopId = "stop-brynhildr",
            runnerState = "Running",
            message = "Traveling to Brynhildr.",
            worldName = "Brynhildr",
            pluginVersion = "1.0.159.53063",
            clientTimestampUtc = DateTimeOffset.UtcNow,
        });
    progress.EnsureSuccessStatusCode();

    var recentResponse = await SendWithKeyAsync(
        client,
        HttpMethod.Get,
        "/marketmafioso/api/acquisition/requests/recent",
        "client-secret");
    recentResponse.EnsureSuccessStatusCode();
    var recent = await recentResponse.Content.ReadFromJsonAsync<JsonElement>();

    var payload = recent.GetProperty("requests")[0];
    Assert.Equal("attempt-001", payload.GetProperty("latestAttemptId").GetString());
    Assert.Equal(1, payload.GetProperty("latestAttemptSequence").GetInt32());
    Assert.Equal("Traveling", payload.GetProperty("latestAttemptPhase").GetString());
}
```

- [ ] **Step 2: Run the focused server test and confirm it fails**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~RecentQueueIncludesLatestAttemptProjection" -v minimal
```

Expected: compile failure or assertion failure because `attemptId` fields are not supported.

- [ ] **Step 3: Create the server attempt DTOs**

Create `MarketMafioso.Server/MarketAcquisitionAttemptModels.cs`:

```csharp
namespace MarketMafioso.Server;

public sealed record MarketAcquisitionAttemptEventRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public string AttemptId { get; init; } = string.Empty;
    public long EventSequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? RouteStopId { get; init; }
    public string? WorldName { get; init; }
    public string? PluginVersion { get; init; }
    public DateTimeOffset ClientTimestampUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventResult
{
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public string Result { get; init; } = "accepted";
    public string? Reason { get; init; }
}

public static class MarketAcquisitionAttemptEventResults
{
    public const string Accepted = "accepted";
    public const string Replayed = "replayed";
    public const string StaleAttempt = "stale_attempt";
    public const string RequestTerminal = "request_terminal";
    public const string SupersededAttempt = "superseded_attempt";
}
```

- [ ] **Step 4: Add projection fields to `MarketAcquisitionRequestView` and claim view**

Add fields to both `MarketAcquisitionRequestView` and `MarketAcquisitionClaimView`:

```csharp
public string? LatestAttemptId { get; init; }
public long? LatestAttemptSequence { get; init; }
public string? LatestAttemptEventType { get; init; }
public string? LatestAttemptPhase { get; init; }
public string? LatestAttemptWorld { get; init; }
public string? LatestAttemptResult { get; init; }
public string? LatestAttemptPluginVersion { get; init; }
```

Copy these fields in `ToClaimView(...)` once the store populates them in Task 2.

- [ ] **Step 5: Re-run the focused server test**

Run the same filtered command. Expected: still fails because storage and endpoint mapping are not implemented yet, but DTO compile errors should be gone.

## Task 2: Add Server Attempt Storage And Idempotency

**Files:**
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add failing tests for idempotent attempt events**

Add tests:

```csharp
[Fact]
public async Task ProgressAttemptEventIsIdempotentForSameBody()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "attempt-idempotent-same");

    var body = new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "req-attempt-001-1-progress",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-001",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        routeStopId = "stop-brynhildr",
        runnerState = "Running",
        message = "Traveling.",
        worldName = "Brynhildr",
        pluginVersion = "1.0.159.53063",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    };

    var first = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", body);
    var second = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", body);

    first.EnsureSuccessStatusCode();
    second.EnsureSuccessStatusCode();
}

[Fact]
public async Task ProgressAttemptEventRejectsSameIdempotencyKeyWithDifferentBody()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "attempt-idempotent-different");

    var first = new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "req-attempt-001-1-progress",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-001",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        routeStopId = "stop-brynhildr",
        runnerState = "Running",
        message = "Traveling.",
        worldName = "Brynhildr",
        pluginVersion = "1.0.159.53063",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    };
    var second = new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "req-attempt-001-1-progress",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-001",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        routeStopId = "stop-brynhildr",
        runnerState = "Running",
        message = "Different payload.",
        worldName = "Brynhildr",
        pluginVersion = "1.0.159.53063",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    };

    await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", first);
    var conflict = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", second);

    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    var text = await conflict.Content.ReadAsStringAsync();
    Assert.Contains("Idempotency key was already used with a different request body", text, StringComparison.Ordinal);
}

[Fact]
public async Task ProgressAttemptEventRejectsSameAttemptSequenceWithDifferentIdempotencyKey()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "attempt-sequence-conflict");

    var first = new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "attempt-sequence-key-a",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-001",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        runnerState = "Running",
        message = "First sequence payload.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    };
    var second = new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "attempt-sequence-key-b",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-001",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        runnerState = "Running",
        message = "Second sequence payload.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    };

    var accepted = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", first);
    accepted.EnsureSuccessStatusCode();

    var conflict = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", second);

    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    var text = await conflict.Content.ReadAsStringAsync();
    Assert.Contains("Attempt event sequence was already used", text, StringComparison.Ordinal);
}
```

- [ ] **Step 1a: Add a server test helper for accepted requests**

The current test file has `CreateAndClaimAsync(...)`, but not `CreateAcceptedRequestAsync(...)`. Add this helper near `CreateAndClaimAsync(...)` before using it in the new tests:

```csharp
private static async Task<(string RequestId, string ClaimToken)> CreateAcceptedRequestAsync(
    HttpClient client,
    string idempotencyKey)
{
    var claimed = await CreateAndClaimAsync(client, idempotencyKey);
    var accept = await SendWithKeyAsync(
        client,
        HttpMethod.Post,
        $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
        "client-secret",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = $"{idempotencyKey}-accept",
        });
    accept.EnsureSuccessStatusCode();
    return claimed;
}
```

- [ ] **Step 2: Add failing tests for stale attempt handling**

Add:

```csharp
[Fact]
public async Task LateOldAttemptProgressAfterNewAttemptIsClassifiedStale()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "attempt-stale");

    var first = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "attempt-a-1",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-a",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        runnerState = "Running",
        message = "Attempt A.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });
    first.EnsureSuccessStatusCode();

    var second = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "attempt-b-1",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-b",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        runnerState = "Running",
        message = "Attempt B.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });
    second.EnsureSuccessStatusCode();

    var stale = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "attempt-a-2",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-a",
        eventSequence = 2,
        eventType = "progress",
        phase = "SearchingItem",
        runnerState = "Running",
        message = "Old attempt woke up.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });

    stale.EnsureSuccessStatusCode();
    var json = await stale.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("stale_attempt", json.GetProperty("result").GetString());
}
```

- [ ] **Step 2a: Add a compatibility test for pre-attempt plugin progress**

Add:

```csharp
[Fact]
public async Task LegacyProgressWithoutAttemptIdStillWorksDuringMigration()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "legacy-progress");

    var progress = await SendWithKeyAsync(
        client,
        HttpMethod.Post,
        $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
        "client-secret",
        new
        {
            claimToken = claimed.ClaimToken,
            idempotencyKey = "legacy-progress-1",
            runnerState = "Running",
            message = "Legacy plugin progress.",
        });

    progress.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await progress.Content.ReadAsStringAsync());
    Assert.Equal("Running", json.RootElement.GetProperty("status").GetString());
}
```

- [ ] **Step 3: Run the new focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Attempt" -v minimal
```

Expected: fails because storage and endpoint handling do not exist yet.

- [ ] **Step 4: Extend SQLite initialization**

In `Initialize()`, add:

```sql
CREATE TABLE IF NOT EXISTS acquisition_request_attempts (
    attempt_id TEXT NOT NULL PRIMARY KEY,
    request_id TEXT NOT NULL,
    plugin_instance_id TEXT NULL,
    status TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    ended_at_utc TEXT NULL,
    latest_sequence INTEGER NOT NULL DEFAULT 0,
    latest_phase TEXT NULL,
    latest_world TEXT NULL,
    latest_message TEXT NULL,
    latest_result TEXT NULL,
    plugin_version TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_acquisition_attempts_request
    ON acquisition_request_attempts(request_id, started_at_utc DESC);

CREATE TABLE IF NOT EXISTS acquisition_attempt_events (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    request_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE,
    plugin_instance_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    phase TEXT NOT NULL,
    route_stop_id TEXT NULL,
    world_name TEXT NULL,
    payload_json TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    result TEXT NOT NULL,
    client_timestamp_utc TEXT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_acquisition_attempt_events_attempt_sequence
    ON acquisition_attempt_events(attempt_id, sequence);
```

- [ ] **Step 5: Add store method `ApplyAttemptLifecycleAsync`**

Keep existing `ApplyLifecycleAsync` for accept/reject during the first slice. Add a new method for progress/complete/fail:

```csharp
public Task<MarketAcquisitionAttemptEventResult?> ReportAttemptProgressAsync(
    string id,
    MarketAcquisitionAttemptEventRequest request,
    CancellationToken cancellationToken) =>
    ApplyAttemptLifecycleAsync(
        id,
        "progress",
        MarketAcquisitionStatuses.Running,
        [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
        request,
        cancellationToken);
```

Repeat equivalent wrappers for complete and fail. The body of `ApplyAttemptLifecycleAsync` must:

- validate claim token, idempotency key, attempt id, sequence, event type, and phase;
- serialize `MarketAcquisitionAttemptEventRequest`;
- replay same idempotency key plus same payload;
- reject same key plus different payload;
- before inserting, check `(attempt_id, sequence)` manually and return a structured conflict if the sequence already exists with a different idempotency key or payload; do not rely on the SQLite unique constraint to throw;
- insert or update `acquisition_request_attempts`;
- classify events for an older attempt as `stale_attempt` once a different attempt has a later `started_at_utc` for the same request;
- update request status only for accepted non-stale events;
- append `acquisition_attempt_events`;
- return `MarketAcquisitionAttemptEventResult` with `Result = accepted`, `replayed`, or `stale_attempt`.

- [ ] **Step 6: Update `Program.cs` lifecycle handlers without breaking old clients**

For progress, complete, and fail endpoints, first deserialize the body as `JsonElement` and check whether it contains a non-empty `attemptId`.

- If `attemptId` exists, deserialize as `MarketAcquisitionAttemptEventRequest`, call the new attempt store methods, and return `MarketAcquisitionAttemptEventResult`.
- If `attemptId` is missing, keep the legacy behavior for now: deserialize as `MarketAcquisitionLifecycleRequest`, call the existing `ReportProgressAsync` / `CompleteAsync` / `FailAsync`, and return a top-level `MarketAcquisitionRequestView`.

Add a comment in the handler:

```csharp
// Compatibility path for deployed plugins that predate execution-attempt events.
// Remove after the client and server deployment cadence has carried attempt-aware progress everywhere.
```

Keep accept/reject on the old request lifecycle DTO. Do not return `400` for missing attempt id until the compatibility path is intentionally removed in a later cleanup.

- [ ] **Step 7: Update queue projection**

Update `ListRecentAsync`, `ListPendingAsync`, `GetByIdAsync`, and related read helpers to join the latest `acquisition_attempt_events` row and populate:

```csharp
LatestAttemptId
LatestAttemptSequence
LatestAttemptEventType
LatestAttemptPhase
LatestAttemptWorld
LatestAttemptResult
LatestAttemptPluginVersion
```

- [ ] **Step 8: Run focused server tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Attempt|FullyQualifiedName~RecentQueueIncludesLatestAttemptProjection" -v minimal
```

Expected: all new attempt tests pass.

## Task 3: Add Plugin Attempt Event Reporting

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketAcquisitionAttemptModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteProgressReporter.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteProgressReporterTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`

- [ ] **Step 1: Add failing reporter tests**

Add:

```csharp
[Fact]
public void CreateAttemptId_ReturnsStableOpaqueIdentifierShape()
{
    var attemptId = MarketAcquisitionRouteProgressReporter.CreateAttemptId();

    Assert.StartsWith("attempt-", attemptId, StringComparison.Ordinal);
    Assert.True(attemptId.Length > "attempt-".Length);
}

[Fact]
public void CreateAttemptIdempotencyKey_IncludesRequestAttemptSequenceAndAction()
{
    var key = MarketAcquisitionRouteProgressReporter.CreateAttemptIdempotencyKey(
        "request-123",
        "attempt-abc",
        42,
        MarketAcquisitionRouteProgressReporter.ProgressAction);

    Assert.Equal("market-acquisition:request-123:attempt-abc:42:progress", key);
}
```

- [ ] **Step 2: Run the focused reporter tests**

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteProgressReporterTests" -v minimal
```

Expected: fails because methods do not exist.

- [ ] **Step 3: Implement attempt id and idempotency key helpers**

In `MarketAcquisitionRouteProgressReporter` add:

```csharp
public static string CreateAttemptId() => $"attempt-{Guid.NewGuid():N}";

public static string CreateAttemptIdempotencyKey(
    string requestId,
    string attemptId,
    long sequence,
    string action)
{
    if (string.IsNullOrWhiteSpace(requestId))
        throw new ArgumentException("Request id is required.", nameof(requestId));
    if (string.IsNullOrWhiteSpace(attemptId))
        throw new ArgumentException("Attempt id is required.", nameof(attemptId));
    if (sequence < 1)
        throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be one or greater.");
    if (string.IsNullOrWhiteSpace(action))
        throw new ArgumentException("Action is required.", nameof(action));

    return $"market-acquisition:{requestId}:{attemptId}:{sequence}:{action}";
}
```

Keep `CreateIdempotencyKey(...)` temporarily if other code still uses it; mark it for removal only after `MainWindow` is switched.

- [ ] **Step 4: Add plugin DTOs**

Create `MarketMafioso/MarketAcquisition/MarketAcquisitionAttemptModels.cs`:

```csharp
namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionAttemptEventRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public string AttemptId { get; init; } = string.Empty;
    public long EventSequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? RouteStopId { get; init; }
    public string? WorldName { get; init; }
    public string? PluginVersion { get; init; }
    public DateTimeOffset ClientTimestampUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventResult
{
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public string Result { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
```

- [ ] **Step 5: Update request client tests**

Add a test that the client posts attempt fields. Use the existing fake handler style in `MarketAcquisitionRequestClientTests`:

```csharp
[Fact]
public async Task ReportProgressAsync_PostsAttemptEventFields()
{
    HttpRequestMessage? captured = null;
    using var httpClient = new HttpClient(new DelegateHandler(request =>
    {
        captured = request;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new MarketAcquisitionAttemptEventResult
            {
                Result = "accepted",
                Request = new MarketAcquisitionRequestView { Id = "request-1", Status = "Running" },
            }),
        };
    }));

    var client = new MarketAcquisitionRequestClient(httpClient);
    await client.ReportAttemptProgressAsync(
        "https://example.test/marketmafioso/api/inventory",
        "secret",
        "request-1",
        "claim-token",
        "market-acquisition:request-1:attempt-1:1:progress",
        "plugin-test-instance",
        "attempt-1",
        1,
        "Traveling",
        "stop-brynhildr",
        "Running",
        "Traveling.",
        "Brynhildr",
        "1.0.159.53063",
        CancellationToken.None);

    var json = await captured!.Content!.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("attempt-1", json.GetProperty("attemptId").GetString());
    Assert.Equal(1, json.GetProperty("eventSequence").GetInt64());
    Assert.Equal("Traveling", json.GetProperty("phase").GetString());
}
```

Adjust `DelegateHandler` construction to match the helper that already exists in the file.

- [ ] **Step 6: Update `MarketAcquisitionRequestClient`**

Change progress/complete/fail signatures to include attempt fields and deserialize `MarketAcquisitionAttemptEventResult`. For compatibility with callers, add new methods first:

```csharp
public Task<MarketAcquisitionAttemptEventResult> ReportAttemptProgressAsync(
    string serverUrl,
    string clientApiKey,
    string requestId,
    string claimToken,
    string idempotencyKey,
    string pluginInstanceId,
    string attemptId,
    long eventSequence,
    string phase,
    string? routeStopId,
    string runnerState,
    string? message,
    string? worldName,
    string? pluginVersion,
    CancellationToken cancellationToken) =>
    PostAttemptLifecycleAsync(
        serverUrl,
        clientApiKey,
        requestId,
        "progress",
        new MarketAcquisitionAttemptEventRequest
        {
            ClaimToken = claimToken,
            IdempotencyKey = idempotencyKey,
            PluginInstanceId = pluginInstanceId,
            AttemptId = attemptId,
            EventSequence = eventSequence,
            EventType = "progress",
            Phase = phase,
            RouteStopId = routeStopId,
            RunnerState = runnerState,
            Message = message,
            WorldName = worldName,
            PluginVersion = pluginVersion,
            ClientTimestampUtc = DateTimeOffset.UtcNow,
        },
        cancellationToken);
```

Add equivalent `CompleteAttemptAsync` and `FailAttemptAsync`. Keep old methods until `MainWindow` is moved, then remove or leave them unused depending on compiler references.

- [ ] **Step 7: Run focused plugin tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteProgressReporterTests|FullyQualifiedName~MarketAcquisitionRequestClientTests" -v minimal
```

Expected: pass after client updates.

## Task 4: Move MainWindow Route Reporting To Attempts

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteProgressReporter.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteProgressReporterTests.cs`

- [ ] **Step 1: Add attempt state fields**

In `MainWindow`, replace or supplement route nonce fields with:

```csharp
private string guidedRouteAttemptId = string.Empty;
private long guidedRouteAttemptSequence = 0;
private string? knownStaleAttemptId;
```

- [ ] **Step 2: Create a fresh attempt on route start and restart**

At the start of the route launch path, before the first progress report:

```csharp
guidedRouteAttemptId = MarketAcquisitionRouteProgressReporter.CreateAttemptId();
guidedRouteAttemptSequence = 0;
knownStaleAttemptId = null;
```

Do the same for restart. Do not create a new attempt for pause/resume inside the same route execution.

- [ ] **Step 3: Replace old idempotency generation**

In `ReportGuidedRouteProgress`, increment sequence once per outbound event and create the idempotency key:

```csharp
var sequence = Interlocked.Increment(ref guidedRouteAttemptSequence);
var action = MarketAcquisitionRouteProgressReporter.ResolveAction(marketAcquisitionRouteRunner.State.ToString());
var idempotencyKey = MarketAcquisitionRouteProgressReporter.CreateAttemptIdempotencyKey(
    claimedAcquisitionRequest.Id,
    guidedRouteAttemptId,
    sequence,
    action);
```

If the field type prevents `Interlocked` on the existing context, use a lock already protecting route state or increment on the UI thread before scheduling the async report.

- [ ] **Step 4: Send attempt lifecycle calls**

Replace old `ReportProgressAsync`, `CompleteAsync`, and `FailAsync` calls with the new attempt-aware client methods. Include:

- `attemptId`: `guidedRouteAttemptId`
- `eventSequence`: sequence from Step 3
- `pluginInstanceId`: `config.PluginInstanceId`
- `phase`: current route phase or runner state string
- `routeStopId`: stable active stop id if available, otherwise a deterministic string derived from the stop index/world for this attempt
- `worldName`: active stop world when available
- `pluginVersion`: visible assembly version string already used in plugin display

- [ ] **Step 5: Handle stale/superseded responses without warning spam**

When the server returns `MarketAcquisitionAttemptEventResult.Result` of `stale_attempt`, `superseded_attempt`, or `request_terminal`:

```csharp
knownStaleAttemptId = guidedRouteAttemptId;
acquisitionStatus = result.Reason ?? "Server ignored stale route progress.";
return;
```

Do not log repeated warnings for the same known stale attempt id. Log one `Info` or `Verbose` event with request id, attempt id, and reason.

- [ ] **Step 6: Remove old route nonce idempotency path once unused**

If `CreateIdempotencyKey(pluginInstanceId, routeNonce, sequence)` has no callers, remove it and update tests. If retained for compatibility, mark it as legacy in a comment and ensure new route code does not use it.

- [ ] **Step 7: Run focused plugin tests and build**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteProgressReporterTests|FullyQualifiedName~MarketAcquisitionRequestClientTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: tests pass, plugin build passes.

## Task 5: Dashboard Queue And Diagnostics Projection

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Test: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 0: Treat both dashboard data surfaces as first-class**

The server currently exposes acquisition queue data through both:

- `/api/acquisition/requests`
- `/acquisition/requests/recent`

Every attempt projection change in this task must update and test both surfaces. Do not rely on only the rendered HTML path or only the Blazor/API path.

- [ ] **Step 1: Add a failing dashboard projection test**

Add:

```csharp
[Fact]
public async Task AcquisitionDashboardShowsAttemptPhaseAndWorld()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "dashboard-attempt-projection");

    await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "dashboard-attempt-1",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-dashboard",
        eventSequence = 1,
        eventType = "progress",
        phase = "Purchasing",
        routeStopId = "stop-brynhildr",
        runnerState = "Running",
        message = "Buying safe listings.",
        worldName = "Brynhildr",
        pluginVersion = "1.0.159.53063",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });

    var page = await client.GetStringAsync("/marketmafioso/acquisition");

    Assert.Contains("Purchasing", page, StringComparison.Ordinal);
    Assert.Contains("Brynhildr", page, StringComparison.Ordinal);
    Assert.Contains("attempt-dashboard", page, StringComparison.Ordinal);
}
```

Add a companion JSON surface test:

```csharp
[Fact]
public async Task AcquisitionApiRequestsIncludesAttemptProjection()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "api-attempt-projection");

    await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "api-attempt-1",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-api",
        eventSequence = 1,
        eventType = "progress",
        phase = "SearchingItem",
        runnerState = "Running",
        message = "Searching.",
        worldName = "Brynhildr",
        pluginVersion = "1.0.159.53063",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });

    var response = await SendWithKeyAsync(
        client,
        HttpMethod.Get,
        "/marketmafioso/api/acquisition/requests",
        "client-secret");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.Contains("attempt-api", json.GetRawText(), StringComparison.Ordinal);
    Assert.Contains("SearchingItem", json.GetRawText(), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run focused dashboard test**

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~AcquisitionDashboardShowsAttemptPhaseAndWorld" -v minimal
```

Expected: fail until rendering includes attempt projection.

- [ ] **Step 3: Add attempt fields to both queue update surfaces**

In the queue update DTO/render helper, include latest attempt fields from `MarketAcquisitionRequestView` for:

- `/api/acquisition/requests`, used by the new dashboard app path.
- `/acquisition/requests/recent`, used by the existing refresh payload.

- [ ] **Step 4: Render attempt summary in queue rows**

For each active or terminal row, show compact detail:

```text
Attempt attempt-dashboard · Purchasing · Brynhildr · seq 1
```

Keep this small and diagnostic; do not make the main queue visually heavy.

- [ ] **Step 5: Add diagnostics timeline endpoint**

Add both logical paths:

```csharp
app.MapGet("/acquisition/requests/{id}/events", ListAcquisitionAttemptEvents);
app.MapGet("/api/acquisition/requests/{id}/events", ListAcquisitionAttemptEvents);
```

Return sanitized recent attempt events for the request, newest first. Require the same dashboard/read auth as the acquisition dashboard.

- [ ] **Step 6: Add endpoint test for event timeline**

```csharp
[Fact]
public async Task AcquisitionAttemptEventsEndpointReturnsSanitizedTimeline()
{
    await using var application = CreateHostedApplication();
    using var client = application.CreateClient();
    var claimed = await CreateAcceptedRequestAsync(client, "attempt-events-endpoint");

    await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress", "client-secret", new
    {
        claimToken = claimed.ClaimToken,
        idempotencyKey = "timeline-attempt-1",
        pluginInstanceId = "plugin-test-instance",
        attemptId = "attempt-timeline",
        eventSequence = 1,
        eventType = "progress",
        phase = "Traveling",
        runnerState = "Running",
        message = "Traveling.",
        clientTimestampUtc = DateTimeOffset.UtcNow,
    });

    var response = await SendWithKeyAsync(
        client,
        HttpMethod.Get,
        $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/events",
        "client-secret");
    response.EnsureSuccessStatusCode();
    var events = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.Equal("attempt-timeline", events.GetProperty("events")[0].GetProperty("attemptId").GetString());
    Assert.False(events.GetRawText().Contains(claimed.ClaimToken, StringComparison.Ordinal));
}
```

Add a second assertion or second test for the API-prefixed diagnostics path:

```csharp
var apiResponse = await SendWithKeyAsync(
    client,
    HttpMethod.Get,
    $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/events",
    "client-secret");
apiResponse.EnsureSuccessStatusCode();
```

- [ ] **Step 7: Run focused server tests**

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Attempt|FullyQualifiedName~AcquisitionDashboardShowsAttemptPhaseAndWorld|FullyQualifiedName~AcquisitionAttemptEventsEndpointReturnsSanitizedTimeline" -v minimal
```

Expected: pass.

## Task 6: Plugin UI Suppression And Recovery Polish

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteProgressReporterTests.cs` if new helper is extracted

- [ ] **Step 1: Extract a pure suppression helper if practical**

If the suppression logic is currently embedded in `MainWindow`, add a small helper in `MarketAcquisitionRouteProgressReporter`:

```csharp
public static bool ShouldSuppressScopeWarning(string? routeState, bool isRouteActive) =>
    isRouteActive &&
    routeState is not null &&
    (routeState.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
     routeState.Equals("Paused", StringComparison.OrdinalIgnoreCase));
```

Test it with route active/inactive cases.

- [ ] **Step 2: Suppress character-scope unavailable during active travel**

In the pickup/fetch UI path, if current character/world is unavailable but a route attempt is active, show:

```text
Route is in transit; request pickup is hidden until the run is stable.
```

Do not show the red "Character scope unavailable" warning during active route travel.

- [ ] **Step 3: Hide irrelevant request controls**

Ensure `Fetch Dashboard Requests`, claim, accept/reject, and prepare controls are hidden or disabled while:

- route attempt is active,
- route attempt is pausing/stopping,
- purchase session is active,
- server just classified current attempt as stale/superseded.

- [ ] **Step 4: Run a plugin build**

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

## Task 7: Documentation, Verification, And Deploy

**Files:**
- Modify: `docs/design/2026-06-25-market-acquisition-roadmap.md`
- Optionally modify: `docs/design/2026-06-27-market-acquisition-run-model.md`

- [ ] **Step 1: Update roadmap progress**

Add a progress note under Phase 8 or a new hardening subsection:

```markdown
### Run Model Hardening

- Request remains the user-facing identity.
- Each route start/restart creates a child execution attempt id.
- Progress idempotency is scoped by request id, attempt id, sequence, and action.
- Stale attempt events are classified rather than producing repeated generic 409 warnings.
- Dashboard diagnostics can inspect request attempt timelines.
```

- [ ] **Step 2: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Attempt|FullyQualifiedName~AcquisitionDashboardShowsAttemptPhaseAndWorld|FullyQualifiedName~AcquisitionAttemptEventsEndpointReturnsSanitizedTimeline" -v minimal
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteProgressReporterTests|FullyQualifiedName~MarketAcquisitionRequestClientTests" -v minimal
```

Expected: all focused tests pass.

- [ ] **Step 3: Run plugin build**

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds with no new warnings.

- [ ] **Step 4: Run format verification**

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes required.

- [ ] **Step 5: Deploy dev plugin only after plugin behavior changes are complete**

```powershell
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

Expected: script reports matching source and target hashes for:

```text
F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\_deployed\MarketMafioso\MarketMafioso.dll
```

- [ ] **Step 6: Manual smoke test**

1. Create a dashboard request.
2. Fetch and accept it in `/mmf`.
3. Prepare a plan.
4. Start route.
5. Confirm client log includes one attempt id and increasing sequences.
6. Restart after a controlled failure and confirm a new attempt id appears under the same request id.
7. Confirm dashboard queue updates latest attempt phase/world.
8. Confirm no repeated generic `409 Conflict` warning appears for stale events.

## Self-Review Notes

- Spec coverage:
  - Request vs attempt identity: Tasks 1-4.
  - Idempotency: Tasks 2-4.
  - Stale/superseded handling: Tasks 2 and 4.
  - UI warning suppression: Task 6.
  - Persistent diagnostics: Task 5.
  - Archive/preset boundary: design-only for now; no implementation task because the user called it feature, not foundation.
- Placeholder scan:
  - No task uses undefined "do the right thing" language without a concrete test or implementation target.
- Type consistency:
  - Plan consistently uses `attemptId`, `eventSequence`, `phase`, `worldName`, and `pluginVersion`.
  - Existing code may temporarily retain `runId`/route nonce names until Task 4 removes or isolates them.
