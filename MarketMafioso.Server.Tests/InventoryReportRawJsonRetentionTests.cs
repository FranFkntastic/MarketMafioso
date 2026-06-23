using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryReportRawJsonRetentionTests
{
    [Fact]
    public async Task RawJsonRoutes_ReturnGoneAfterOriginalJsonIsPruned()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var firstJson = CreateReportJson("Raw One", 2);
        var secondJson = CreateReportJson("Raw Two", 3);
        var thirdJson = CreateReportJson("Raw Three", 4);

        var first = await PostRawJsonAsync(client, firstJson);
        await PostRawJsonAsync(client, secondJson);
        var third = await PostRawJsonAsync(client, thirdJson);

        var pruned = await client.GetAsync($"/reports/{first}/json");
        var retained = await client.GetAsync($"/reports/{third}/json");

        Assert.Equal(HttpStatusCode.Gone, pruned.StatusCode);
        Assert.Contains("raw_json_pruned", await pruned.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        Assert.Equal(
            MinifyJson(thirdJson),
            MinifyJson(await retained.Content.ReadAsStringAsync()));
    }

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
                        ["MarketMafioso:RawJsonRetentionCount"] = "2",
                    });
                });
            });
    }

    private static async Task<string> PostRawJsonAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/inventory", content);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static string CreateReportJson(string characterName, uint itemId) =>
        $$"""
        {
          "metadata": {
            "schemaVersion": 1,
            "sourcePlugin": "MarketMafioso",
            "pluginVersion": "1.0.0.0",
            "generatedAtUtc": "2026-06-23T12:00:00.0000000Z"
          },
          "characterName": "{{characterName}}",
          "homeWorld": "Gilgamesh",
          "timestamp": "2026-06-23T12:00:00.0000000Z",
          "playerInventory": [
            {
              "bagName": "Inventory1",
              "items": [
                {
                  "itemId": {{itemId}},
                  "itemName": "Item {{itemId}}",
                  "quantity": 1,
                  "isHQ": false,
                  "condition": 0
                }
              ]
            }
          ],
          "retainers": []
        }
        """;

    private static string MinifyJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
