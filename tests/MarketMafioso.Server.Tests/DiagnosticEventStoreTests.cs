using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace MarketMafioso.Server.Tests;

public sealed class DiagnosticEventStoreTests
{
    [Fact]
    public async Task DiagnosticEventStore_PersistsEventsAndPrunesOldRows()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:DiagnosticEventRetention", "2"));
        var store = application.Services.GetRequiredService<DiagnosticEventStore>();

        await store.WriteAsync(CreateEvent("server.config", "one"), CancellationToken.None);
        var second = await store.WriteAsync(CreateEvent("acquisition.request", "two"), CancellationToken.None);
        var third = await store.WriteAsync(CreateEvent("acquisition.route", "three"), CancellationToken.None);

        var recent = await store.ListRecentAsync(10, null, null, null, CancellationToken.None);

        Assert.Equal([third.Id, second.Id], recent.Select(e => e.Id).ToArray());
        Assert.DoesNotContain(recent, e => e.Message == "one");
    }

    [Fact]
    public async Task DiagnosticsApi_StreamsSnapshotEventsForLoggedInDashboard()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        var store = application.Services.GetRequiredService<DiagnosticEventStore>();
        await store.WriteAsync(CreateEvent("acquisition.route", "arrived"), CancellationToken.None);

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/diagnostics/events/stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("event: snapshot", body, StringComparison.Ordinal);
        Assert.Contains("arrived", body, StringComparison.Ordinal);
    }

    private static DiagnosticEventCreate CreateEvent(string category, string message) => new()
    {
        Source = "test",
        Category = category,
        Type = "test.event",
        Severity = "Info",
        Outcome = "Succeeded",
        Message = message,
        CorrelationId = "correlation-test",
        PayloadSummaryJson = """{"ok":true}""",
    };

    private static WebApplicationFactory<Program> CreateApplication(params KeyValuePair<string, string?>[] extraConfiguration)
        => ServerTestHost.Create(host =>
        {
            host.Configuration["MarketMafioso:RequireDashboardAuth"] = "true";
            host.Configuration["MarketMafioso:DashboardBootstrapUsername"] = "admin";
            host.Configuration["MarketMafioso:DashboardBootstrapPassword"] = "secret-password";
            foreach (var item in extraConfiguration)
                host.Configuration[item.Key] = item.Value;
        });
}
