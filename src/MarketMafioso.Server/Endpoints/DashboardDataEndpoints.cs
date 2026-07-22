using System.Globalization;
using System.Text.Json;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.Server.Endpoints;

internal static class DashboardDataEndpoints
{
    public static void MapDashboardDataEndpoints(this WebApplication app, bool enableMarketAcquisition)
    {
        app.MapGet("/api/inventory/characters", ListCharacters);
        app.MapGet("/api/inventory/browser", GetInventoryBrowser);
        app.MapGet("/api/inventory/snapshots", ListSnapshots);
        app.MapGet("/api/settings/dashboard", GetSettings);
        app.MapPut("/api/settings/dashboard", SaveSettings);
        app.MapGet("/api/settings/storage", GetStorageSummary);
        app.MapGet("/api/settings/features", () => Results.Ok(new DashboardFeatureFlagsView
        {
            EnableMarketAcquisition = enableMarketAcquisition,
        }));
    }

    private static async Task<IResult> ListCharacters(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        InventoryReportStore store,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var characters = new List<CharacterSummary>();
        foreach (var accountId in accountIds)
        {
            var accountCharacters = await store.ListCharactersAsync(accountId, token);
            characters.AddRange(accountCharacters);
        }

        var serviceAccountLabels = characters
            .Where(character => !string.IsNullOrWhiteSpace(character.ServiceAccountKey))
            .GroupBy(character => character.ServiceAccountKey!, StringComparer.Ordinal)
            .OrderBy(group => group.Min(character => character.Id))
            .Select((group, index) => (group.Key, Label: $"Service Account {index + 1}"))
            .ToDictionary(entry => entry.Key, entry => entry.Label, StringComparer.Ordinal);

        return Results.Ok(characters
            .GroupBy(character => character.Id)
            .Select(group => group.First())
            .OrderByDescending(character => character.LastSeenAt)
            .ThenBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Select(character => new DashboardCharacterOption(
                character.Id,
                character.CharacterName,
                character.HomeWorld,
                character.LastSeenAt,
                character.ServiceAccountKey is { Length: > 0 } key && serviceAccountLabels.TryGetValue(key, out var label)
                    ? label
                    : "Awaiting account evidence"))
            .ToArray());
    }

    private static async Task<IResult> GetInventoryBrowser(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        InventoryReportStore store,
        long? characterId,
        string? snapshotId,
        string? filter,
        string? search,
        string? scope,
        InventoryBrowserMode? mode,
        int? caret,
        CancellationToken token)
    {
        if (characterId != null &&
            !await CanAccessCharacterAsync(context, connectionFactory, characterId.Value, token))
        {
            return Results.NotFound();
        }

        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        StoredInventoryReport? report;
        if (!string.IsNullOrWhiteSpace(snapshotId))
        {
            report = await store.GetAsync(accountIds, snapshotId, token);
            if (report == null)
                return Results.NotFound();
        }
        else
        {
            report = await store.GetLatestAsync(accountIds, characterId, token);
        }

        return Results.Ok(InventoryBrowserViewBuilder.Build(report, filter ?? search, scope, mode ?? InventoryBrowserMode.Items, caret));
    }

    private static async Task<IResult> ListSnapshots(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        InventoryReportStore store,
        long? characterId,
        CancellationToken token)
    {
        if (characterId != null &&
            !await CanAccessCharacterAsync(context, connectionFactory, characterId.Value, token))
        {
            return Results.NotFound();
        }

        var summaries = new List<ReportSummary>();
        foreach (var accountId in await GetAccountIdsAsync(context, connectionFactory, token))
            summaries.AddRange(await store.ListSummariesAsync(accountId, characterId, token));

        return Results.Ok(summaries
            .OrderByDescending(summary => summary.ReceivedAt)
            .Take(500)
            .ToArray());
    }

    private static async Task<IResult> GetSettings(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        var owner = PreferenceOwner(context);
        var settings = await LoadSettingsAsync(connectionFactory, owner, token);
        return settings == null
            ? Results.Ok(new DashboardSettingsView())
            : Results.Ok(settings);
    }

    private static async Task<IResult> SaveSettings(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        DashboardSettingsUpdate update,
        CancellationToken token)
    {
        if (!IsSupportedAcquisitionRegion(update.DefaultRegion))
            return Results.BadRequest(new { error = "unsupported_region" });

        if (update.DefaultWorldMode is not ("Recommended" or "CurrentWorld" or "AllWorldSweep"))
            return Results.BadRequest(new { error = "unsupported_world_mode" });

        if (update.DefaultPickupExpiresSeconds is < 60 or > 3600)
            return Results.BadRequest(new { error = "unsupported_pickup_expiry" });

        if (update.DefaultCharacterId != null &&
            !await CanAccessCharacterAsync(context, connectionFactory, update.DefaultCharacterId.Value, token))
        {
            return Results.BadRequest(new { error = "unknown_character" });
        }

        var now = DateTimeOffset.UtcNow;
        var view = new DashboardSettingsView
        {
            DefaultCharacterId = update.DefaultCharacterId,
            DefaultRegion = update.DefaultRegion,
            DefaultWorldMode = update.DefaultWorldMode,
            DefaultPickupExpiresSeconds = update.DefaultPickupExpiresSeconds,
            UpdatedAtUtc = now,
        };
        var owner = PreferenceOwner(context);
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboard_preferences (
                owner_kind,
                owner_key,
                scope,
                preferences_json,
                updated_at_utc
            )
            VALUES (
                $ownerKind,
                $ownerKey,
                $scope,
                $preferencesJson,
                $updatedAt
            )
            ON CONFLICT(owner_kind, owner_key, scope)
            DO UPDATE SET
                preferences_json = excluded.preferences_json,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
        command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
        command.Parameters.AddWithValue("$scope", owner.Scope);
        command.Parameters.AddWithValue(
            "$preferencesJson",
            JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token);

        return Results.Ok(view);
    }

    private static async Task<IResult> GetStorageSummary(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        InventoryReportStore inventoryStore,
        DiagnosticEventStore diagnostics,
        IConfiguration configuration,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var inventory = await inventoryStore.GetRetentionSummaryAsync(accountIds, token);
        var diagnosticCount = await diagnostics.CountAsync(token);

        return Results.Ok(new ReceiverStorageSummaryView
        {
            SnapshotRetentionCount = configuration.GetValue("MarketMafioso:SnapshotRetentionCount", 500),
            RawJsonRetentionCount = configuration.GetValue("MarketMafioso:RawJsonRetentionCount", 20),
            DiagnosticEventRetentionCount = Math.Max(
                1,
                configuration.GetValue("MarketMafioso:DiagnosticEventRetention", 5000)),
            SnapshotCount = inventory.SnapshotCount,
            RawJsonRetainedCount = inventory.RawJsonRetainedCount,
            RawJsonPrunedCount = inventory.RawJsonPrunedCount,
            DiagnosticEventCount = diagnosticCount,
            NewestSnapshotReceivedAtUtc = inventory.NewestSnapshotReceivedAtUtc,
            OldestSnapshotReceivedAtUtc = inventory.OldestSnapshotReceivedAtUtc,
        });
    }

    private static bool IsSupportedAcquisitionRegion(string region) =>
        region is "North America" or "Europe" or "Japan" or "Oceania";

    private static DashboardPreferenceOwner PreferenceOwner(HttpContext context)
    {
        var userId = DashboardUserId(context);
        return userId == null
            ? new DashboardPreferenceOwner("global", "default", "dashboard")
            : new DashboardPreferenceOwner(
                "dashboard-user",
                userId.Value.ToString(CultureInfo.InvariantCulture),
                "dashboard");
    }

    private static long? DashboardUserId(HttpContext context) =>
        context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) && value is long userId
            ? userId
            : null;

    private static async Task<IReadOnlyList<long>> GetAccountIdsAsync(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        var userId = DashboardUserId(context);
        if (userId == null)
            return [1];

        var accounts = new List<long>();
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id
            FROM dashboard_user_accounts
            WHERE dashboard_user_id = $dashboardUserId
            ORDER BY is_default DESC, account_id
            """;
        command.Parameters.AddWithValue("$dashboardUserId", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            accounts.Add(reader.GetInt64(0));

        return accounts.Count == 0 ? [1] : accounts;
    }

    private static async Task<bool> CanAccessCharacterAsync(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        long characterId,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 1
            FROM characters
            WHERE id = $characterId
              AND account_id IN ({string.Join(", ", accountIds.Select((_, index) => $"$account{index}"))})
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        for (var i = 0; i < accountIds.Count; i++)
            command.Parameters.AddWithValue($"$account{i}", accountIds[i]);

        return await command.ExecuteScalarAsync(token) != null;
    }

    private static async Task<DashboardSettingsView?> LoadSettingsAsync(
        SqliteConnectionFactory connectionFactory,
        DashboardPreferenceOwner owner,
        CancellationToken token)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preferences_json, updated_at_utc
            FROM dashboard_preferences
            WHERE owner_kind = $ownerKind
              AND owner_key = $ownerKey
              AND scope = $scope
            """;
        command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
        command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
        command.Parameters.AddWithValue("$scope", owner.Scope);

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;

        var settings = JsonSerializer.Deserialize<DashboardSettingsView>(
            reader.GetString(0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (settings == null)
            return null;

        return settings with
        {
            UpdatedAtUtc = DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
        };
    }

    private sealed record DashboardPreferenceOwner(string OwnerKind, string OwnerKey, string Scope);
}
