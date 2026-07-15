using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class ClientCredentialManagementEndpointTests
{
    [Fact]
    public async Task CraftArchitectKey_CanBeIssuedUsedAndRevokedWithoutReceiverRestart()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);

        var create = await client.PostAsJsonAsync("/api/settings/client-keys", new ClientCredentialCreateRequest
        {
            Label = "CA Firefox",
            Purpose = "CraftArchitect",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ClientCredentialCreatedView>();
        Assert.NotNull(created);
        Assert.StartsWith("mmf_ca_", created.Secret, StringComparison.Ordinal);

        using var capabilities = RequestWithKey(HttpMethod.Get, "/api/capabilities", created.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(capabilities)).StatusCode);

        using var acquisition = RequestWithKey(HttpMethod.Post, "/api/acquisition/batches", created.Secret);
        acquisition.Content = JsonContent.Create(MarketAcquisitionTestApp.CreateBatchRequest("managed-ca-key"));
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(acquisition)).StatusCode);

        using var inventory = RequestWithKey(HttpMethod.Post, "/api/inventory", created.Secret);
        inventory.Content = JsonContent.Create(new InventoryReport
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "Inventory1",
                    Items = [],
                },
            ],
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(inventory)).StatusCode);

        var revoke = await client.DeleteAsync($"/api/settings/client-keys/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var rejected = RequestWithKey(HttpMethod.Get, "/api/capabilities", created.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(rejected)).StatusCode);
    }

    [Fact]
    public async Task ClientKeyList_ExposesMetadataButNeverReturnsSecretsAgain()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);

        var create = await client.PostAsJsonAsync("/api/settings/client-keys", new ClientCredentialCreateRequest
        {
            Label = "MM plugin",
            Purpose = "MarketMafiosoClient",
        });
        create.EnsureSuccessStatusCode();
        var createdJson = await create.Content.ReadAsStringAsync();
        var created = await create.Content.ReadFromJsonAsync<ClientCredentialCreatedView>();

        var listJson = await client.GetStringAsync("/api/settings/client-keys");

        Assert.NotNull(created);
        Assert.Contains(created.Secret, createdJson, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Secret, listJson, StringComparison.Ordinal);
        Assert.Contains(created.KeyPrefix, listJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientKeyManagement_RequiresDashboardSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings/client-keys")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/settings/client-keys", new ClientCredentialCreateRequest
            {
                Label = "Unauthorized",
                Purpose = "CraftArchitect",
            })).StatusCode);
    }

    private static HttpRequestMessage RequestWithKey(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();
    }

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var databasePath = Path.Combine(contentRoot, "marketmafioso.db");

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = databasePath,
                        ["MarketMafioso:RequireApiKey"] = "true",
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                        ["MarketMafioso:EnableMarketAcquisition"] = "true",
                    }));
            });
    }
}
