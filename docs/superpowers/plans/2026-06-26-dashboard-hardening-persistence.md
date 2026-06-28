# Dashboard Hardening And Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add hybrid server/browser dashboard preferences, acquisition page reload/tab persistence, remove dashboard CSRF overhead, and responsive shell hardening.

**Architecture:** Add a focused SQLite-backed dashboard preference store keyed by dashboard owner, expose dashboard-authenticated preference endpoints, and hydrate the acquisition page from server state first with browser localStorage fallback. Keep transient page state browser-local while durable options such as default character persist on the server and mirror to browser storage.

**Tech Stack:** C# 12, ASP.NET Core minimal APIs, Microsoft.Data.Sqlite, xUnit/WebApplicationFactory tests, server-rendered HTML/CSS/JavaScript.

---

## File Structure

- Create `MarketMafioso.Server/DashboardPreferences.cs` for preference DTOs, owner identity, normalization, and JSON serialization.
- Create `MarketMafioso.Server/DashboardPreferenceStore.cs` for SQLite persistence.
- Modify `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs` to create `dashboard_preferences`.
- Modify `MarketMafioso.Server/Auth/DashboardBasicAuthMiddleware.cs` to expose the authenticated dashboard user ID in `HttpContext.Items`.
- Modify `MarketMafioso.Server/Program.cs` to register the store, add preference endpoints, render options/bootstrap data, persist page state in JavaScript, remove dashboard CSRF plumbing, and update responsive CSS.
- Modify `MarketMafioso.Server.Tests/SqliteSchemaMigratorTests.cs` for schema coverage.
- Create `MarketMafioso.Server.Tests/DashboardPreferenceStoreTests.cs` for store behavior.
- Modify `MarketMafioso.Server.Tests/DashboardAccountAuthTests.cs` for preference endpoint auth.
- Modify `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs` for acquisition page, CSRF removal, and responsive assertions.

---

### Task 1: Add Dashboard Preference Schema

**Files:**
- Modify: `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs`
- Modify: `MarketMafioso.Server.Tests/SqliteSchemaMigratorTests.cs`

- [ ] **Step 1: Write the schema test**

Add this test to `SqliteSchemaMigratorTests`:

```csharp
[Fact]
public async Task MigrateAsync_CreatesDashboardPreferencesTable()
{
    var directory = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var factory = new SqliteConnectionFactory(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketMafioso:DataDirectory"] = directory,
        })
        .Build());
    var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);

    await migrator.MigrateAsync(CancellationToken.None);

    await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
    Assert.True(await TableExistsAsync(connection, "dashboard_preferences"));
    Assert.True(await ColumnExistsAsync(connection, "dashboard_preferences", "owner_kind"));
    Assert.True(await ColumnExistsAsync(connection, "dashboard_preferences", "owner_key"));
    Assert.True(await ColumnExistsAsync(connection, "dashboard_preferences", "scope"));
    Assert.True(await ColumnExistsAsync(connection, "dashboard_preferences", "preferences_json"));
    Assert.True(await ColumnExistsAsync(connection, "dashboard_preferences", "updated_at_utc"));
}
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~SqliteSchemaMigratorTests.MigrateAsync_CreatesDashboardPreferencesTable"
```

Expected: fail because `dashboard_preferences` does not exist.

- [ ] **Step 3: Add the table**

In `SqliteSchemaMigrator.MigrationSql`, add the table after `dashboard_user_accounts`:

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

- [ ] **Step 4: Verify the schema test passes**

Run the same filtered `dotnet test` command. Expected: pass.

### Task 2: Add Preference Models And Store

**Files:**
- Create: `MarketMafioso.Server/DashboardPreferences.cs`
- Create: `MarketMafioso.Server/DashboardPreferenceStore.cs`
- Create: `MarketMafioso.Server.Tests/DashboardPreferenceStoreTests.cs`

- [ ] **Step 1: Write store tests**

Create `DashboardPreferenceStoreTests.cs` with tests for save/load, missing preferences, normalization, and local owner keys:

```csharp
namespace MarketMafioso.Server.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server;
using MarketMafioso.Server.Sqlite;

public sealed class DashboardPreferenceStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsAcquisitionPreferences()
    {
        var (store, owner) = await CreateStoreAsync();
        var preferences = new AcquisitionDashboardPreferences
        {
            SchemaVersion = 1,
            DefaultCharacterId = 42,
            AutoRefreshEnabled = false,
            RestoreQueueFilters = true,
        };

        var saved = await store.SaveAcquisitionAsync(owner, preferences, CancellationToken.None);
        var loaded = await store.GetAcquisitionAsync(owner, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.DefaultCharacterId);
        Assert.False(loaded.AutoRefreshEnabled);
        Assert.True(loaded.RestoreQueueFilters);
        Assert.NotEqual(default, saved.UpdatedAtUtc);
    }

    [Fact]
    public async Task GetAcquisitionAsync_ReturnsNullWhenMissing()
    {
        var (store, owner) = await CreateStoreAsync();

        var loaded = await store.GetAcquisitionAsync(owner, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAcquisitionAsync_NormalizesDefaults()
    {
        var (store, owner) = await CreateStoreAsync();

        var saved = await store.SaveAcquisitionAsync(
            owner,
            new AcquisitionDashboardPreferences { SchemaVersion = 1 },
            CancellationToken.None);

        Assert.True(saved.AutoRefreshEnabled);
        Assert.True(saved.RestoreQueueFilters);
    }

    [Fact]
    public void ReceiverLocalOwner_UsesStableKey()
    {
        var owner = DashboardPreferenceOwner.ReceiverLocal();

        Assert.Equal("receiver-local", owner.OwnerKind);
        Assert.Equal("default", owner.OwnerKey);
    }

    private static async Task<(DashboardPreferenceStore Store, DashboardPreferenceOwner Owner)> CreateStoreAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMafioso:DataDirectory"] = directory,
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        return (new DashboardPreferenceStore(factory), DashboardPreferenceOwner.DashboardUser(7));
    }
}
```

- [ ] **Step 2: Run failing store tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~DashboardPreferenceStoreTests"
```

Expected: compile fails because the new types do not exist.

- [ ] **Step 3: Implement `DashboardPreferences.cs`**

Create:

```csharp
namespace MarketMafioso.Server;

public readonly record struct DashboardPreferenceOwner(string OwnerKind, string OwnerKey)
{
    public static DashboardPreferenceOwner DashboardUser(long userId) =>
        new("dashboard-user", userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static DashboardPreferenceOwner ReceiverLocal() =>
        new("receiver-local", "default");
}

public sealed class AcquisitionDashboardPreferences
{
    public const int CurrentSchemaVersion = 1;
    public const string Scope = "acquisition-dashboard";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long? DefaultCharacterId { get; init; }
    public bool AutoRefreshEnabled { get; init; } = true;
    public bool RestoreQueueFilters { get; init; } = true;
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public AcquisitionDashboardPreferences Normalize(DateTimeOffset updatedAtUtc) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        DefaultCharacterId = DefaultCharacterId,
        AutoRefreshEnabled = AutoRefreshEnabled,
        RestoreQueueFilters = RestoreQueueFilters,
        UpdatedAtUtc = updatedAtUtc,
    };
}
```

- [ ] **Step 4: Implement `DashboardPreferenceStore.cs`**

Create:

```csharp
namespace MarketMafioso.Server;

using System.Globalization;
using System.Text.Json;
using MarketMafioso.Server.Sqlite;

public sealed class DashboardPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory connectionFactory;

    public DashboardPreferenceStore(SqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task<AcquisitionDashboardPreferences?> GetAcquisitionAsync(
        DashboardPreferenceOwner owner,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preferences_json
            FROM dashboard_preferences
            WHERE owner_kind = $ownerKind
              AND owner_key = $ownerKey
              AND scope = $scope;
            """;
        command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
        command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
        command.Parameters.AddWithValue("$scope", AcquisitionDashboardPreferences.Scope);

        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AcquisitionDashboardPreferences>(json, JsonOptions);
    }

    public async Task<AcquisitionDashboardPreferences> SaveAcquisitionAsync(
        DashboardPreferenceOwner owner,
        AcquisitionDashboardPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (preferences.SchemaVersion != AcquisitionDashboardPreferences.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported dashboard preference schema version.", nameof(preferences));

        var normalized = preferences.Normalize(DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboard_preferences (owner_kind, owner_key, scope, preferences_json, updated_at_utc)
            VALUES ($ownerKind, $ownerKey, $scope, $preferencesJson, $updatedAtUtc)
            ON CONFLICT(owner_kind, owner_key, scope) DO UPDATE SET
                preferences_json = excluded.preferences_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
        command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
        command.Parameters.AddWithValue("$scope", AcquisitionDashboardPreferences.Scope);
        command.Parameters.AddWithValue("$preferencesJson", json);
        command.Parameters.AddWithValue("$updatedAtUtc", normalized.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return normalized;
    }
}
```

- [ ] **Step 5: Verify store tests pass**

Run the filtered `DashboardPreferenceStoreTests` command. Expected: pass.

### Task 3: Expose Dashboard User Context

**Files:**
- Modify: `MarketMafioso.Server/Auth/DashboardBasicAuthMiddleware.cs`
- Modify: `MarketMafioso.Server.Tests/DashboardAccountAuthTests.cs`

- [ ] **Step 1: Add middleware item key**

In `DashboardBasicAuthMiddleware`, add:

```csharp
public const string DashboardUserIdItemKey = "MarketMafioso.DashboardUserId";
```

In `InvokeAsync`, after valid credentials are confirmed, set:

```csharp
context.Items[DashboardUserIdItemKey] = credentials.Value.UserId;
```

To support that, change credential validation to return `long?`:

```csharp
var userId = await GetValidDashboardUserIdAsync(credentials.Value.Username, credentials.Value.Password, context.RequestAborted);
if (userId == null)
{
    await ChallengeAsync(context);
    return;
}

context.Items[DashboardUserIdItemKey] = userId.Value;
await next(context);
```

Rename `IsValidDashboardUserAsync` to `GetValidDashboardUserIdAsync` and return `userId` after the password check.

- [ ] **Step 2: Include preference routes as dashboard routes**

Extend `IsDashboardRoute`:

```csharp
request.Path.StartsWithSegments("/dashboard/preferences", StringComparison.OrdinalIgnoreCase)
```

for GET and PUT/POST-style methods used by the preference endpoints.

- [ ] **Step 3: Verify auth tests still pass**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~DashboardAccountAuthTests"
```

Expected: pass.

### Task 4: Add Preference Endpoints

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/DashboardAccountAuthTests.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Register the store**

Add to service registration in `Program.cs`:

```csharp
builder.Services.AddSingleton<DashboardPreferenceStore>();
```

- [ ] **Step 2: Add owner resolver helper**

Add near the other dashboard helpers:

```csharp
static DashboardPreferenceOwner ResolveDashboardPreferenceOwner(HttpContext context, IConfiguration configuration)
{
    if (context.Items.TryGetValue(DashboardBasicAuthMiddleware.DashboardUserIdItemKey, out var userIdValue) &&
        userIdValue is long userId)
    {
        return DashboardPreferenceOwner.DashboardUser(userId);
    }

    if (!configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth"))
        return DashboardPreferenceOwner.ReceiverLocal();

    throw new UnauthorizedAccessException("Dashboard preferences require an authenticated dashboard user.");
}
```

- [ ] **Step 3: Add GET and PUT route tests**

In `MarketAcquisitionRequestEndpointTests`, add tests:

```csharp
[Fact]
public async Task DashboardPreferences_ReturnsNotFoundWhenNoServerStateExists()
{
    await using var application = CreateHostedApplication(
        extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
    using var client = application.CreateClient();

    var response = await client.GetAsync("/api/marketmafioso/dashboard/preferences/acquisition");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task DashboardPreferences_SaveAndLoadAcquisitionPreferences()
{
    await using var application = CreateHostedApplication(
        extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
    using var client = application.CreateClient();

    using var save = new HttpRequestMessage(HttpMethod.Put, "/api/marketmafioso/dashboard/preferences/acquisition");
    save.Content = JsonContent.Create(new
    {
        schemaVersion = 1,
        defaultCharacterId = 1,
        autoRefreshEnabled = false,
        restoreQueueFilters = true,
    });

    var saved = await client.SendAsync(save);
    var loaded = await client.GetAsync("/api/marketmafioso/dashboard/preferences/acquisition");

    Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
    var json = await loaded.Content.ReadAsStringAsync();
    Assert.Contains("\"defaultCharacterId\":1", json, StringComparison.Ordinal);
    Assert.Contains("\"autoRefreshEnabled\":false", json, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Implement endpoints**

Add routes after `/acquisition/requests/recent`:

```csharp
app.MapGet("/dashboard/preferences/acquisition", async (
    HttpContext context,
    DashboardPreferenceStore preferences,
    IConfiguration configuration,
    CancellationToken token) =>
{
    var owner = ResolveDashboardPreferenceOwner(context, configuration);
    var saved = await preferences.GetAcquisitionAsync(owner, token);
    return saved == null
        ? Results.NotFound(new { error = "preferences_not_found" })
        : Results.Ok(saved);
});

app.MapPut("/dashboard/preferences/acquisition", async (
    HttpContext context,
    HttpRequest request,
    DashboardPreferenceStore preferences,
    IConfiguration configuration,
    CancellationToken token) =>
{
    var incoming = await JsonSerializer.DeserializeAsync<AcquisitionDashboardPreferences>(
        request.Body,
        new JsonSerializerOptions(JsonSerializerDefaults.Web),
        token);
    if (incoming == null)
        return Results.BadRequest(new { error = "invalid_preferences" });

    var owner = ResolveDashboardPreferenceOwner(context, configuration);
    var saved = await preferences.SaveAcquisitionAsync(owner, incoming, token);
    return Results.Ok(saved);
});
```

Dashboard authorization comes from the dashboard auth middleware/trusted external auth boundary, not CSRF headers.

- [ ] **Step 5: Verify preference endpoint tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~DashboardPreferences"
```

Expected: pass.

### Task 5: Render Bootstrap Data And Options UI

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add page contract tests**

Extend the acquisition dashboard render test to assert:

```csharp
Assert.Contains("id=\"dashboardOptionsButton\"", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("id=\"dashboardOptionsPanel\"", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("id=\"dashboardBootstrap\"", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("marketmafioso.dashboard.acquisition.v1", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("/api/marketmafioso/dashboard/preferences/acquisition", acquisitionPage, StringComparison.Ordinal);
```

- [ ] **Step 2: Add bootstrap model rendering**

In `RenderAcquisitionDashboard`, build a bootstrap JSON string with:

```csharp
var bootstrapJson = JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    preferencesUrl = AppUrl(pathBase, "/dashboard/preferences/acquisition"),
    refreshUrl,
    selectedCharacterId,
    selectedCharacter = selectedCharacter == null ? null : new
    {
        id = selectedCharacter.Id,
        characterName = selectedCharacter.CharacterName,
        homeWorld = selectedCharacter.HomeWorld,
    },
    characters = characters.Select(character => new
    {
        id = character.Id,
        characterName = character.CharacterName,
        homeWorld = character.HomeWorld,
        label = CharacterLabel(character),
    }),
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
```

Render it before the main script:

```html
<script id="dashboardBootstrap" type="application/json">{{Html(bootstrapJson)}}</script>
```

- [ ] **Step 3: Add options panel markup**

In the queue pane header, add:

```html
<button id="dashboardOptionsButton" type="button" class="small-button">Options</button>
```

Add the panel near the end of `.shell`:

```html
<section id="dashboardOptionsPanel" class="options-panel" hidden aria-label="Dashboard options">
    <div class="options-card">
        <div class="pane-head">
            <h2>Dashboard Options</h2>
            <button id="dashboardOptionsClose" type="button">Close</button>
        </div>
        <div class="pane-body">
            <label>Default character<select id="optionDefaultCharacter"></select></label>
            <label class="check-row"><input id="optionAutoRefresh" type="checkbox"> Auto-refresh request queue</label>
            <label class="check-row"><input id="optionRestoreFilters" type="checkbox"> Restore queue filters</label>
            <div class="button-row">
                <button id="dashboardOptionsSave" class="primary" type="button">Save</button>
                <button id="dashboardOptionsClearLocal" type="button">Clear Local State</button>
                <button id="dashboardOptionsResetServer" type="button">Reset Server Options</button>
            </div>
            <div id="dashboardOptionsStatus" class="stage-status" role="status" aria-live="polite"></div>
        </div>
    </div>
</section>
```

- [ ] **Step 4: Add minimal options CSS**

Add:

```css
.options-panel {
    position: fixed;
    inset: 0;
    display: grid;
    place-items: center;
    padding: 18px;
    background: rgba(0, 0, 0, .45);
    z-index: 10;
}
.options-panel[hidden] { display: none; }
.options-card {
    width: min(520px, 100%);
    border: 1px solid var(--line);
    border-radius: 8px;
    background: var(--panel);
    box-shadow: 0 18px 60px rgba(0, 0, 0, .35);
}
.check-row {
    display: flex;
    align-items: center;
    grid-template-columns: none;
    gap: 8px;
    margin: 10px 0;
}
.check-row input {
    width: auto;
    height: auto;
}
```

- [ ] **Step 5: Verify render contract**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests"
```

Expected: pass after implementation.

### Task 6: Add Browser State Manager

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add JavaScript contract assertions**

Assert the page contains:

```csharp
Assert.Contains("loadDashboardPreferences", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("saveDashboardPreferences", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("persistAcquisitionPageState", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("window.addEventListener('storage'", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("stagedQueueRows", acquisitionPage, StringComparison.Ordinal);
```

- [ ] **Step 2: Add state constants**

At the top of the acquisition dashboard script, add:

```javascript
const dashboardStateKey = 'marketmafioso.dashboard.acquisition.v1';
const dashboardBootstrap = JSON.parse(document.getElementById('dashboardBootstrap')?.textContent || '{}');
let dashboardState = loadBrowserDashboardState();
let acquisitionQueue = Array.isArray(dashboardState.stagedQueueRows) ? dashboardState.stagedQueueRows : [];
let selectedAcquisitionItem = dashboardState.selectedItem || null;
let acquisitionSearchTimer = null;
let acquisitionRefreshTimer = null;
```

Replace the existing `const acquisitionQueue = []; let selectedAcquisitionItem = null;` declarations.

- [ ] **Step 3: Add browser load/save helpers**

Add:

```javascript
function loadBrowserDashboardState() {
    try {
        const parsed = JSON.parse(window.localStorage.getItem(dashboardStateKey) || '{}');
        return parsed && parsed.schemaVersion === 1 ? parsed : { schemaVersion: 1 };
    } catch {
        return { schemaVersion: 1 };
    }
}

function persistAcquisitionPageState() {
    const form = document.querySelector('.request-form');
    const formData = form ? Object.fromEntries(new FormData(form).entries()) : {};
    const nextState = {
        ...dashboardState,
        schemaVersion: 1,
        queueFilterText: document.getElementById('acquisitionQueueFilter')?.value || '',
        queueStatusFilter: document.getElementById('acquisitionStatusFilter')?.value || 'All statuses',
        requestForm: formData,
        selectedItem: selectedAcquisitionItem,
        stagedQueueRows: acquisitionQueue
    };
    dashboardState = nextState;
    window.localStorage.setItem(dashboardStateKey, JSON.stringify(nextState));
}
```

- [ ] **Step 4: Hydrate form, filters, and staged queue**

Add:

```javascript
function hydrateAcquisitionPageState() {
    if (dashboardState.restoreQueueFilters !== false) {
        document.getElementById('acquisitionQueueFilter').value = dashboardState.queueFilterText || '';
        document.getElementById('acquisitionStatusFilter').value = dashboardState.queueStatusFilter || 'All statuses';
    }
    const form = document.querySelector('.request-form');
    if (form && dashboardState.requestForm) {
        Object.entries(dashboardState.requestForm).forEach(([name, value]) => {
            if (name === 'idempotencyKey') return;
            const input = form.elements.namedItem(name);
            if (input) input.value = value;
        });
    }
    if (selectedAcquisitionItem) {
        document.getElementById('selectedItemId').value = selectedAcquisitionItem.itemId || '';
        document.getElementById('selectedItemName').value = selectedAcquisitionItem.itemName || selectedAcquisitionItem.name || '';
        document.getElementById('resolvedAcquisitionItem').value = `${selectedAcquisitionItem.itemName || selectedAcquisitionItem.name} (${selectedAcquisitionItem.itemId})`;
    }
    renderAcquisitionQueueRows();
    applyAcquisitionQueueFilter();
}
```

- [ ] **Step 5: Wire persistence events**

Add listeners:

```javascript
document.querySelector('.request-form')?.addEventListener('input', persistAcquisitionPageState);
document.querySelector('.request-form')?.addEventListener('change', persistAcquisitionPageState);
document.getElementById('acquisitionQueueFilter')?.addEventListener('input', () => {
    applyAcquisitionQueueFilter();
    persistAcquisitionPageState();
});
document.getElementById('acquisitionStatusFilter')?.addEventListener('change', () => {
    applyAcquisitionQueueFilter();
    persistAcquisitionPageState();
});
window.addEventListener('storage', event => {
    if (event.key !== dashboardStateKey) return;
    dashboardState = loadBrowserDashboardState();
    acquisitionQueue = Array.isArray(dashboardState.stagedQueueRows) ? dashboardState.stagedQueueRows : [];
    selectedAcquisitionItem = dashboardState.selectedItem || null;
    hydrateAcquisitionPageState();
});
```

Remove duplicate old filter listeners that call only `applyAcquisitionQueueFilter`.

- [ ] **Step 6: Persist queue changes**

After `acquisitionQueue.push(row)`, `acquisitionQueue.splice`, and successful complete stage, call `persistAcquisitionPageState()`. On full success before navigation, clear staged rows:

```javascript
acquisitionQueue.length = 0;
persistAcquisitionPageState();
window.location.href = '{{Html(AppUrl(pathBase, "/acquisition"))}}';
```

- [ ] **Step 7: Verify script contract tests**

Run the focused acquisition endpoint tests. Expected: pass.

### Task 7: Add Server Preference Hydration And Options Save

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add JavaScript contract assertions**

Assert:

```csharp
Assert.Contains("fetch(dashboardBootstrap.preferencesUrl", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("method: 'PUT'", acquisitionPage, StringComparison.Ordinal);
Assert.DoesNotContain("X-CSRF-Token", acquisitionPage, StringComparison.Ordinal);
Assert.Contains("serverPreferenceUpdatedAtUtc", acquisitionPage, StringComparison.Ordinal);
```

- [ ] **Step 2: Add option initialization**

Add:

```javascript
function fillOptionsCharacterList() {
    const select = document.getElementById('optionDefaultCharacter');
    if (!select) return;
    select.innerHTML = '<option value="">Use latest character</option>' +
        (dashboardBootstrap.characters || []).map(character =>
            `<option value="${character.id}">${escapeHtml(character.label)}</option>`).join('');
}

function applyDurablePreferences(preferences) {
    dashboardState = {
        ...dashboardState,
        defaultCharacterId: preferences.defaultCharacterId ?? null,
        autoRefreshEnabled: preferences.autoRefreshEnabled !== false,
        restoreQueueFilters: preferences.restoreQueueFilters !== false,
        serverPreferenceUpdatedAtUtc: preferences.updatedAtUtc || dashboardState.serverPreferenceUpdatedAtUtc
    };
    document.getElementById('optionDefaultCharacter').value = dashboardState.defaultCharacterId || '';
    document.getElementById('optionAutoRefresh').checked = dashboardState.autoRefreshEnabled !== false;
    document.getElementById('optionRestoreFilters').checked = dashboardState.restoreQueueFilters !== false;
    applyDefaultCharacter();
    persistAcquisitionPageState();
}
```

- [ ] **Step 3: Add server load**

Add:

```javascript
async function loadDashboardPreferences() {
    try {
        const response = await fetch(dashboardBootstrap.preferencesUrl, {
            headers: { 'Accept': 'application/json' },
            cache: 'no-store',
            credentials: 'same-origin'
        });
        if (response.status === 404) {
            applyDurablePreferences(dashboardState);
            return;
        }
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const preferences = await response.json();
        applyDurablePreferences(preferences);
    } catch {
        applyDurablePreferences(dashboardState);
        setDashboardOptionsStatus('Using browser-saved dashboard options.', false);
    }
}
```

- [ ] **Step 4: Add server save**

Add:

```javascript
async function saveDashboardPreferences() {
    const preferences = {
        schemaVersion: 1,
        defaultCharacterId: document.getElementById('optionDefaultCharacter').value
            ? Number(document.getElementById('optionDefaultCharacter').value)
            : null,
        autoRefreshEnabled: document.getElementById('optionAutoRefresh').checked,
        restoreQueueFilters: document.getElementById('optionRestoreFilters').checked
    };
    applyDurablePreferences(preferences);
    try {
        const response = await fetch(dashboardBootstrap.preferencesUrl, {
            method: 'PUT',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/json'
            },
            credentials: 'same-origin',
            body: JSON.stringify(preferences)
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        applyDurablePreferences(await response.json());
        setDashboardOptionsStatus('Dashboard options saved.', false, true);
    } catch {
        persistAcquisitionPageState();
        setDashboardOptionsStatus('Server save failed. Options are saved in this browser.', true);
    }
}
```

- [ ] **Step 5: Add default character application**

Add:

```javascript
function applyDefaultCharacter() {
    const defaultId = Number(dashboardState.defaultCharacterId || 0);
    if (!defaultId) return;
    const character = (dashboardBootstrap.characters || []).find(item => Number(item.id) === defaultId);
    if (!character) return;
    const selector = document.querySelector('.selected-character select');
    if (selector) selector.value = String(defaultId);
    const form = document.querySelector('.request-form');
    if (form?.elements.namedItem('targetCharacterName')) form.elements.namedItem('targetCharacterName').value = character.characterName || '';
    if (form?.elements.namedItem('targetWorld')) form.elements.namedItem('targetWorld').value = character.homeWorld || '';
}
```

- [ ] **Step 6: Wire Options buttons**

Add:

```javascript
document.getElementById('dashboardOptionsButton')?.addEventListener('click', () => {
    document.getElementById('dashboardOptionsPanel').hidden = false;
});
document.getElementById('dashboardOptionsClose')?.addEventListener('click', () => {
    document.getElementById('dashboardOptionsPanel').hidden = true;
});
document.getElementById('dashboardOptionsSave')?.addEventListener('click', saveDashboardPreferences);
document.getElementById('dashboardOptionsClearLocal')?.addEventListener('click', () => {
    window.localStorage.removeItem(dashboardStateKey);
    dashboardState = { schemaVersion: 1 };
    acquisitionQueue = [];
    selectedAcquisitionItem = null;
    renderAcquisitionQueueRows();
    setDashboardOptionsStatus('Local page state cleared.', false, true);
});
document.getElementById('dashboardOptionsResetServer')?.addEventListener('click', async () => {
    document.getElementById('optionDefaultCharacter').value = '';
    document.getElementById('optionAutoRefresh').checked = true;
    document.getElementById('optionRestoreFilters').checked = true;
    await saveDashboardPreferences();
});
```

- [ ] **Step 7: Initialize in correct order**

At the bottom of the script:

```javascript
fillOptionsCharacterList();
hydrateAcquisitionPageState();
loadDashboardPreferences();
startOrStopAcquisitionRefresh();
```

Implement:

```javascript
function startOrStopAcquisitionRefresh() {
    if (acquisitionRefreshTimer) window.clearInterval(acquisitionRefreshTimer);
    if (dashboardState.autoRefreshEnabled !== false) {
        acquisitionRefreshTimer = window.setInterval(refreshAcquisitionQueue, 3000);
    }
}
```

Remove the old unconditional `window.setInterval(refreshAcquisitionQueue, 3000);`.

- [ ] **Step 8: Verify tests**

Run acquisition endpoint tests. Expected: pass.

### Task 8: Remove Dashboard CSRF Plumbing

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add CSRF-removal tests**

Add tests proving dashboard pages do not render CSRF fields/cookies/scripts, queue refresh JSON does not return a CSRF token, and browser mutation posts succeed or redirect normally without a CSRF field.

Example queue refresh test:

```csharp
[Fact]
public async Task AcquisitionDashboardQueueRefreshDoesNotReturnCsrfToken()
{
    await using var application = CreateHostedApplication(
        extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
    using var client = application.CreateClient();

    using var response = await client.GetAsync("/api/marketmafioso/acquisition/requests/recent");
    using var recentJson = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    Assert.False(recentJson.RootElement.TryGetProperty("csrfToken", out _));
}
```

- [ ] **Step 2: Remove server token issuance and validation**

Delete the dashboard CSRF cookie/token helpers and remove CSRF validation from browser dashboard mutation routes:

- `SetCsrfCookie`
- `IsValidDashboardPostAsync`
- `HasValidOrigin`
- browser-form CSRF checks in report delete/delete-all
- browser-form CSRF checks in acquisition create/cancel/resend

- [ ] **Step 3: Remove rendered CSRF fields and client token plumbing**

Remove hidden `csrf` inputs, `mmf_csrf` cookie assumptions, refresh-payload `csrfToken`, staged-row token injection, stale-token retry handling, and any `invalid_csrf` UI copy from the dashboard JavaScript.

- [ ] **Step 4: Verify CSRF-removal tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests.AcquisitionDashboardRendersControlSurfaceWithRequestQueue|FullyQualifiedName~MarketAcquisitionRequestEndpointTests.AcquisitionDashboardQueueRefreshDoesNotReturnCsrfToken|FullyQualifiedName~InventoryReportViewEndpointTests.HostedMode_DashboardDeleteDoesNotRequireCsrf|FullyQualifiedName~InventoryReportViewEndpointTests.HostedMode_ReportDetailsDoesNotRenderCsrf"
```

Expected: CSRF-removal tests pass.

### Task 9: Responsive Shell Hardening

**Files:**
- Modify: `MarketMafioso.Server/Program.cs`
- Modify: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] **Step 1: Add CSS regression assertion**

Assert:

```csharp
Assert.Contains("grid-template-rows: auto 1fr auto", acquisitionPage, StringComparison.Ordinal);
```

- [ ] **Step 2: Change shell CSS**

Replace:

```css
.shell { min-height: 100vh; display: grid; grid-template-rows: 54px 1fr 30px; }
```

with:

```css
.shell { min-height: 100vh; display: grid; grid-template-rows: auto 1fr auto; }
```

- [ ] **Step 3: Add mobile queue guard**

Keep the table wrapper scrollable and add under `max-width: 720px`:

```css
.queue-pane .table-wrap { max-width: calc(100vw - 24px); }
.statusbar { min-height: 30px; }
```

- [ ] **Step 4: Verify acquisition render tests**

Run acquisition endpoint tests. Expected: pass.

### Task 10: Full Verification And Browser Smoke

**Files:**
- All files touched above.

- [ ] **Step 1: Verify branches before final changes**

Run:

```powershell
git status --short --branch
git rev-list --left-right --count dashboard-hardening...local-dev
```

Expected: current branch is `dashboard-hardening`; branch delta is `0 0`.

- [ ] **Step 2: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~DashboardPreferenceStoreTests|FullyQualifiedName~MarketAcquisitionRequestEndpointTests|FullyQualifiedName~DashboardAccountAuthTests"
```

Expected: pass.

- [ ] **Step 3: Run full server tests**

Run:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
```

Expected: pass.

- [ ] **Step 4: Run format check**

Run:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes required.

- [ ] **Step 5: Browser smoke**

Run the server, open `/acquisition`, and verify:

- Options opens and saves default character.
- Reload keeps default character, filters, form fields, and staged queue rows.
- A second tab receives local page-state changes through `storage`.
- Server preferences win over browser fallback after saving options.
- Dashboard mutation posts do not require CSRF fields, and dashboard pages/refresh payloads do not emit CSRF tokens.
- Narrow viewport does not overlap header/footer with dashboard content.

- [ ] **Step 6: Commit**

Commit the implementation:

```powershell
git add MarketMafioso.Server MarketMafioso.Server.Tests docs/superpowers/specs/2026-06-26-dashboard-hardening-persistence-design.md docs/superpowers/plans/2026-06-26-dashboard-hardening-persistence.md
git commit -m "Harden acquisition dashboard persistence"
```

- [ ] **Step 7: Push and deploy if plugin behavior changed**

If only server/dashboard code changed, push `dashboard-hardening` for review. If plugin DLL behavior changed unexpectedly, run `MarketMafioso/tools/Deploy-DevPlugin.ps1`; this plan should not require plugin deployment.
