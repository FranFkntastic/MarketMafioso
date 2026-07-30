using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        var changedBag = Assert.Single(delta.UpsertedPlayerBags);
        Assert.Equal("Inventory2", changedBag.BagName);
        var reconstructed = InventoryReportDeltaApplier.Apply("base-1", before, delta);
        Assert.Equal(9u, reconstructed.PlayerInventory[1].Items[0].Quantity);
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

    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static InventoryReport CreateReport() => new()
    {
        Metadata = new InventoryReportMetadata
        {
            SchemaVersion = 4,
            SourcePlugin = "MarketMafioso",
            PluginVersion = "test",
            GeneratedAtUtc = "2026-07-30T12:00:00Z",
        },
        CharacterName = "Delta Tester",
        HomeWorld = "Siren",
        ServiceAccountKey = "test-account",
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
