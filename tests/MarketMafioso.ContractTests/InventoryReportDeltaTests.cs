using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketMafioso.Server.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MarketMafioso.Server.ContractTests;

public sealed class InventoryReportDeltaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Builder_SendsOnlyChangedBagAndApplierReconstructsSnapshot()
    {
        var before = CreateReport();
        var after = before with
        {
            Metadata = before.Metadata with { GeneratedAtUtc = "2026-07-30T12:01:00Z" },
            Timestamp = "2026-07-30T12:01:00Z",
            PlayerInventory = before.PlayerInventory
                .Select((bag, index) => index == 1
                    ? bag with
                    {
                        ObservedAtUtc = "2026-07-30T12:01:00Z",
                        Items = [bag.Items[0] with { Quantity = 9 }],
                    }
                    : bag)
                .ToList(),
        };

        var result = InventoryReportDeltaBuilder.Build("base-1", before, after);

        Assert.Equal(InventoryDeltaBuildDisposition.Delta, result.Disposition);
        var delta = Assert.IsType<InventoryReportDelta>(result.Delta);
        Assert.Equal(2, delta.ServiceAccountNumber);
        var changedBag = Assert.Single(delta.UpsertedPlayerBags);
        Assert.Equal("Inventory2", changedBag.BagName);
        var reconstructed = InventoryReportDeltaApplier.Apply("base-1", before, delta);
        Assert.Equal(9u, reconstructed.PlayerInventory[1].Items[0].Quantity);
        Assert.Equal(2, reconstructed.ServiceAccountNumber);
        Assert.Equal(after.Timestamp, reconstructed.Timestamp);
        Assert.True(
            JsonSerializer.Serialize(delta, JsonOptions).Length <
            JsonSerializer.Serialize(after, JsonOptions).Length / 2);
    }

    [Fact]
    public void Builder_SkipsObservationTimestampOnlyChanges()
    {
        var before = CreateReport();
        var after = before with
        {
            Metadata = before.Metadata with { GeneratedAtUtc = "2026-07-30T12:01:00Z" },
            Timestamp = "2026-07-30T12:01:00Z",
            PlayerInventory = before.PlayerInventory
                .Select(bag => bag with { ObservedAtUtc = "2026-07-30T12:01:00Z" })
                .ToList(),
        };

        var result = InventoryReportDeltaBuilder.Build("base-1", before, after);

        Assert.Equal(InventoryDeltaBuildDisposition.Unchanged, result.Disposition);
        Assert.Null(result.Delta);
    }

    [Fact]
    public void EvidencePredicate_DistinguishesUnavailableFromObservedEmptyStorage()
    {
        var unavailable = new InventoryReport();
        var observedEmpty = unavailable with
        {
            PlayerStorage = new StorageSourceEvidence
            {
                RequestedSources = ["Inventory1"],
                ObservedSources = ["Inventory1"],
            },
        };

        Assert.False(InventoryReportEvidence.HasSnapshotEvidence(unavailable));
        Assert.True(InventoryReportEvidence.HasSnapshotEvidence(observedEmpty));
    }

    [Fact]
    public async Task DeltaEndpoint_ReconstructsAndStoresFullSnapshot()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var before = CreateReport();
        var fullResponse = await client.PostAsJsonAsync("/inventory", before, JsonOptions);
        fullResponse.EnsureSuccessStatusCode();
        var baseId = await ReadIdAsync(fullResponse);
        var after = before with
        {
            Timestamp = "2026-07-30T12:02:00Z",
            PlayerInventory = before.PlayerInventory
                .Select((bag, index) => index == 0
                    ? bag with { Items = [bag.Items[0] with { Quantity = 7 }] }
                    : bag)
                .ToList(),
        };
        var delta = InventoryReportDeltaBuilder.Build(baseId, before, after).Delta!;

        var deltaResponse = await client.PostAsJsonAsync("/inventory/delta", delta, JsonOptions);

        deltaResponse.EnsureSuccessStatusCode();
        var deltaId = await ReadIdAsync(deltaResponse);
        var raw = await client.GetStringAsync($"/reports/{deltaId}/json");
        var stored = JsonSerializer.Deserialize<InventoryReport>(raw, JsonOptions)!;
        Assert.Equal(7u, stored.PlayerInventory[0].Items[0].Quantity);
        Assert.Equal(20, stored.PlayerInventory.Count);
        Assert.DoesNotContain("baseSnapshotId", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeltaEndpoint_IncompleteIdentityDoesNotCreateSelectableCharacter()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var before = CreateReport() with { HomeWorld = null };
        var fullResponse = await client.PostAsJsonAsync("/inventory", before, JsonOptions);
        fullResponse.EnsureSuccessStatusCode();
        var baseId = await ReadIdAsync(fullResponse);
        var after = before with
        {
            Timestamp = "2026-07-30T12:02:00Z",
            PlayerGil = 42,
        };
        var delta = InventoryReportDeltaBuilder.Build(baseId, before, after).Delta!;

        var deltaResponse = await client.PostAsJsonAsync("/inventory/delta", delta, JsonOptions);

        deltaResponse.EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<DashboardCharacterOption[]>("/api/inventory/characters"))!);
    }

    [Fact]
    public async Task DeltaEndpoint_LegacyFullAndDeltaCannotEraseConfirmedAccountNumber()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var current = CreateReport();
        (await client.PostAsJsonAsync("/inventory", current, JsonOptions)).EnsureSuccessStatusCode();
        var legacy = current with
        {
            Metadata = current.Metadata with { SchemaVersion = 4 },
            ServiceAccountKey = "legacy-profile-key",
            ServiceAccountNumber = null,
            Timestamp = "2026-07-30T12:01:00Z",
        };
        var legacyFullResponse = await client.PostAsJsonAsync("/inventory", legacy, JsonOptions);
        legacyFullResponse.EnsureSuccessStatusCode();
        var legacyBaseId = await ReadIdAsync(legacyFullResponse);
        var legacyAfter = legacy with
        {
            Timestamp = "2026-07-30T12:02:00Z",
            PlayerGil = 77,
        };
        var delta = InventoryReportDeltaBuilder.Build(legacyBaseId, legacy, legacyAfter).Delta!;

        var deltaResponse = await client.PostAsJsonAsync("/inventory/delta", delta, JsonOptions);

        deltaResponse.EnsureSuccessStatusCode();
        var character = Assert.Single((await client.GetFromJsonAsync<DashboardCharacterOption[]>("/api/inventory/characters"))!);
        Assert.Equal(2, character.ServiceAccountNumber);
    }

    [Fact]
    public async Task DeltaEndpoint_MissingBaseRequestsReconciliation()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var delta = InventoryReportDeltaBuilder.Build("missing-base", CreateReport(), CreateReport() with
        {
            PlayerGil = 42,
        }).Delta!;

        var response = await client.PostAsJsonAsync("/api/inventory/delta", delta, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("inventory_delta_base_missing", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeltaEndpoint_StaleRetainedBaseCannotRegressCurrentHead()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var before = CreateReport();
        var fullResponse = await client.PostAsJsonAsync("/inventory", before, JsonOptions);
        fullResponse.EnsureSuccessStatusCode();
        var baseId = await ReadIdAsync(fullResponse);
        var current = before with { Timestamp = "2026-07-30T12:02:00Z", PlayerGil = 42 };
        var currentDelta = InventoryReportDeltaBuilder.Build(baseId, before, current).Delta!;
        var currentResponse = await client.PostAsJsonAsync("/inventory/delta", currentDelta, JsonOptions);
        currentResponse.EnsureSuccessStatusCode();
        var currentId = await ReadIdAsync(currentResponse);
        var stale = before with { Timestamp = "2026-07-30T12:01:00Z", PlayerGil = 7 };
        var staleDelta = InventoryReportDeltaBuilder.Build(baseId, before, stale).Delta!;

        var staleResponse = await client.PostAsJsonAsync("/inventory/delta", staleDelta, JsonOptions);
        var stored = await client.GetFromJsonAsync<StoredInventoryReport>($"/api/reports/{currentId}");

        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Contains("inventory_delta_base_stale", await staleResponse.Content.ReadAsStringAsync());
        Assert.Equal((ulong)42, stored!.Report.PlayerGil);
    }

    [Fact]
    public async Task FullEndpoint_AcceptsObservedEmptyStorageAndRejectsUnavailableCapture()
    {
        await using var application = ServerTestHost.Create();
        using var client = application.CreateClient();
        var unavailable = new InventoryReport
        {
            CharacterName = "Empty Tester",
            HomeWorld = "Siren",
        };
        var observedEmpty = unavailable with
        {
            PlayerStorage = new StorageSourceEvidence
            {
                RequestedSources = ["Inventory1"],
                ObservedSources = ["Inventory1"],
            },
        };

        var unavailableResponse = await client.PostAsJsonAsync("/inventory", unavailable, JsonOptions);
        var observedResponse = await client.PostAsJsonAsync("/inventory", observedEmpty, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, unavailableResponse.StatusCode);
        observedResponse.EnsureSuccessStatusCode();
        var snapshotId = await ReadIdAsync(observedResponse);
        var storedJson = await client.GetStringAsync($"/reports/{snapshotId}/json");
        var stored = JsonSerializer.Deserialize<InventoryReport>(storedJson, JsonOptions)!;
        Assert.Empty(stored.PlayerInventory);
        Assert.Equal(["Inventory1"], stored.PlayerStorage.ObservedSources);
    }

    [Fact]
    public async Task FullEndpoint_ReturnsRetryableServiceUnavailableWhenAnotherWriterOwnsSqlite()
    {
        var host = ServerTestHost.CreateConfiguration();
        host.Configuration["MarketMafioso:SqliteBusyTimeoutSeconds"] = "1";
        await using var application = ServerTestHost.Create(host);
        using var client = application.CreateClient();
        var connectionFactory = application.Services.GetRequiredService<SqliteConnectionFactory>();
        await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = "UPDATE accounts SET display_name = display_name WHERE id = 1";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var response = await client.PostAsJsonAsync("/inventory", CreateReport(), JsonOptions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Contains("receiver_busy", await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static InventoryReport CreateReport() => new()
    {
        Metadata = new InventoryReportMetadata
        {
            SchemaVersion = 5,
            SourcePlugin = "MarketMafioso",
            PluginVersion = "test",
            GeneratedAtUtc = "2026-07-30T12:00:00Z",
        },
        CharacterName = "Delta Tester",
        HomeWorld = "Siren",
        ServiceAccountKey = "test-account",
        ServiceAccountNumber = 2,
        PlayerGil = 10,
        Timestamp = "2026-07-30T12:00:00Z",
        PlayerInventory = Enumerable.Range(1, 20)
            .Select(index => Bag($"Inventory{index}", (uint)index, (uint)(index + 1)))
            .ToList(),
        Retainers = [],
        PlayerStorage = new StorageSourceEvidence
        {
            RequestedSources = ["Inventory"],
            ObservedSources = ["Inventory"],
        },
    };

    private static InventoryBag Bag(string name, uint itemId, uint quantity) => new()
    {
        BagName = name,
        Location = "Inventory",
        ObservedAtUtc = "2026-07-30T12:00:00Z",
        Items =
        [
            new ItemSlot
            {
                ItemId = itemId,
                ItemName = $"Item {itemId}",
                Quantity = quantity,
            },
        ],
    };
}
