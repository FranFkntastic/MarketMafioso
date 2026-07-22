using System.Text.Json;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.Tests.Quartermaster;

public sealed class QuartermasterIpcClientTests
{
    [Fact]
    public void GetSnapshot_WhenCapabilitiesChannelIsUnavailable_DoesNotInvokeAnyFunction()
    {
        var adapter = new FakeQuartermasterIpcAdapter { HasCapabilities = false };
        using var client = new QuartermasterIpcClient(adapter);

        Assert.False(client.TryGetSnapshot(out var snapshot, out var error));

        Assert.Null(snapshot);
        Assert.Contains("not loaded", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adapter.CapabilitiesCalls);
        Assert.Equal(0, adapter.SnapshotCalls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema\":\"wrong\",\"providerInstanceId\":\"provider-a\",\"revision\":1}")]
    public void GetSnapshot_RejectsMalformedOrUnsupportedCapabilities(string capabilitiesJson)
    {
        var adapter = new FakeQuartermasterIpcAdapter { CapabilitiesJson = capabilitiesJson };
        using var client = new QuartermasterIpcClient(adapter);

        Assert.False(client.TryGetSnapshot(out _, out var error));

        Assert.NotEmpty(error);
        Assert.Equal(0, adapter.SnapshotCalls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema\":\"wrong\"}")]
    public void GetSnapshot_RejectsMalformedOrUnsupportedSnapshot(string snapshotJson)
    {
        var adapter = ReadyAdapter("provider-a", 1, snapshotJson);
        using var client = new QuartermasterIpcClient(adapter);

        Assert.False(client.TryGetSnapshot(out _, out var error));

        Assert.NotEmpty(error);
        Assert.Equal(1, adapter.SnapshotCalls);
    }

    [Fact]
    public void GetSnapshot_DropsCacheAcrossProviderUnloadAndReload()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var first, out _));
        Assert.Equal("provider-a", first!.ProviderInstanceId);

        adapter.HasCapabilities = false;
        Assert.False(client.TryGetSnapshot(out var unavailable, out _));
        Assert.Null(unavailable);

        adapter.HasCapabilities = true;
        adapter.CapabilitiesJson = CapabilitiesJson("provider-b", 1);
        adapter.SnapshotJson = SnapshotJson("provider-b", 1, "Second");
        Assert.True(client.TryGetSnapshot(out var reloaded, out _));

        Assert.Equal("provider-b", reloaded!.ProviderInstanceId);
        Assert.Equal("Second", Assert.Single(reloaded.Retainers).RetainerName);
        Assert.NotSame(first, reloaded);
    }

    [Fact]
    public void GetSnapshot_ProviderExceptionClearsCacheInsteadOfReturningStaleData()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        using var client = new QuartermasterIpcClient(adapter);
        Assert.True(client.TryGetSnapshot(out var first, out _));

        adapter.CapabilitiesException = new InvalidOperationException("provider unloaded");
        Assert.False(client.TryGetSnapshot(out var stale, out var error));
        Assert.Null(stale);
        Assert.Contains("provider unloaded", error, StringComparison.Ordinal);

        adapter.CapabilitiesException = null;
        Assert.True(client.TryGetSnapshot(out var recovered, out _));
        Assert.NotSame(first, recovered);
        Assert.Equal(2, adapter.SnapshotCalls);
    }

    [Fact]
    public void GetSnapshot_RetriesWhenProviderChangesBetweenCalls()
    {
        var adapter = ReadyAdapter("provider-b", 1, SnapshotJson("provider-b", 1, "Second"));
        adapter.CapabilitiesResponses.Enqueue(CapabilitiesJson("provider-a", 1));
        adapter.CapabilitiesResponses.Enqueue(CapabilitiesJson("provider-b", 1));
        adapter.CapabilitiesResponses.Enqueue(CapabilitiesJson("provider-b", 1));
        adapter.CapabilitiesResponses.Enqueue(CapabilitiesJson("provider-b", 1));
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var snapshot, out var error), error);

        Assert.Equal("provider-b", snapshot!.ProviderInstanceId);
        Assert.Equal(2, adapter.SnapshotCalls);
    }

    [Fact]
    public void ChangedAtNewRevision_InvalidatesCachedSnapshot()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        using var client = new QuartermasterIpcClient(adapter);
        Assert.True(client.TryGetSnapshot(out var first, out _));
        Assert.True(client.TryGetSnapshot(out var cached, out _));
        Assert.Same(first, cached);
        Assert.Equal(1, adapter.SnapshotCalls);

        adapter.CapabilitiesJson = CapabilitiesJson("provider-a", 2);
        adapter.SnapshotJson = SnapshotJson("provider-a", 2, "Updated");
        adapter.RaiseChanged(JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.ChangedSchema,
            providerInstanceId = "provider-a",
            revision = 2,
        }));

        Assert.True(client.TryGetSnapshot(out var updated, out var error), error);
        Assert.Equal(2, updated!.Revision);
        Assert.Equal("Updated", Assert.Single(updated.Retainers).RetainerName);
        Assert.Equal(2, adapter.SnapshotCalls);
    }

    [Fact]
    public void GetOperation_ParsesProviderContractAndRecognizesSuccessAsTerminal()
    {
        var adapter = ReadyAdapter("provider-a", 3, SnapshotJson("provider-a", 3, "First"));
        adapter.OperationJson = JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.OperationSchema,
            providerInstanceId = "provider-a",
            operationId = "operation-1",
            requestId = "request-1",
            owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
            status = "partially_succeeded",
            revision = 3,
            updatedAtUtc = "2026-07-21T12:00:00Z",
            completedAtUtc = "2026-07-21T12:00:00Z",
            message = "Retrieved available stock.",
        });
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetOperation("operation-1", new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"), out var operation, out var error), error);

        Assert.Equal("request-1", operation!.RequestId);
        Assert.True(operation.IsTerminal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuartermasterIpcClient.ShortageRequestSchema)]
    public void Submit_RejectsMissingOrEchoedRequestSchema(string? responseSchema)
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = responseSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                status = "accepted",
            });
        };
        using var client = new QuartermasterIpcClient(adapter);
        var request = new QuartermasterShortageRequest(
            "request-1",
            "operation-1",
            DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            true,
            [new QuartermasterShortageTarget(100, "Elm Lumber", 50, 30)]);

        Assert.False(client.TrySubmitShortages(request, out var acknowledgement, out var error));

        Assert.Null(acknowledgement);
        Assert.Contains("schema", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_SerializesImmediateExecutionAuthorization()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        adapter.CapabilitiesJson = CapabilitiesJson(
            "provider-a",
            1,
            QuartermasterIpcClient.AutomaticRetrievalCapability);
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                status = "accepted",
            });
        };
        using var client = new QuartermasterIpcClient(adapter);
        var request = new QuartermasterShortageRequest(
            "request-1",
            "operation-1",
            DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            true,
            [new QuartermasterShortageTarget(100, "Elm Lumber", 50, 30)]);

        Assert.True(client.TrySubmitShortages(request, out _, out var error), error);

        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        Assert.True(submitted.RootElement.GetProperty("executeImmediately").GetBoolean());
    }

    [Fact]
    public void Submit_WhenAutomaticRetrievalIsNotAdvertised_OmitsAuthorizationField()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                status = "accepted",
            });
        };
        using var client = new QuartermasterIpcClient(adapter);
        var request = new QuartermasterShortageRequest(
            "request-1",
            "operation-1",
            DateTimeOffset.UtcNow,
            new QuartermasterOwner(100, 40, "Wei Ning", "Maduin"),
            true,
            [new QuartermasterShortageTarget(100, "Elm Lumber", 50, 30)]);

        Assert.True(client.TrySubmitShortages(request, out var acknowledgement, out var error), error);

        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        Assert.False(submitted.RootElement.TryGetProperty("executeImmediately", out _));
        Assert.False(acknowledgement!.ExecuteImmediately);
    }

    [Fact]
    public void GetCapabilities_StoresAdvertisedCapabilities()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        adapter.CapabilitiesJson = CapabilitiesJson(
            "provider-a",
            1,
            QuartermasterIpcClient.AutomaticRetrievalCapability);
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetCapabilities(out var capabilities, out var error), error);

        Assert.Equal([QuartermasterIpcClient.AutomaticRetrievalCapability], capabilities!.Capabilities.ToArray());
    }

    [Fact]
    public void Snapshot_ReturnsOnlyImmutableCollections()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        using var client = new QuartermasterIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var snapshot, out _));

        Assert.Equal("System.Collections.Immutable.ImmutableArray`1", snapshot!.Retainers.GetType().GetGenericTypeDefinition().FullName);
        Assert.Equal("System.Collections.Immutable.ImmutableArray`1", snapshot.Retainers[0].Bags.GetType().GetGenericTypeDefinition().FullName);
        Assert.Equal("System.Collections.Immutable.ImmutableArray`1", snapshot.Retainers[0].Bags[0].Items.GetType().GetGenericTypeDefinition().FullName);
        Assert.Equal(["Crystals", "Inventory1"], snapshot.PlayerRequestedSources.ToArray());
        Assert.Equal(["RetainerPage1"], snapshot.Retainers[0].ObservedSources.ToArray());
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 11, 55, 0, TimeSpan.Zero), snapshot.Retainers[0].GilObservedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 11, 54, 0, TimeSpan.Zero), snapshot.Retainers[0].Bags[0].ObservedAtUtc);
    }

    [Fact]
    public void Dispose_UnsubscribesChangedHandlerOnce()
    {
        var adapter = ReadyAdapter("provider-a", 1, SnapshotJson("provider-a", 1, "First"));
        var client = new QuartermasterIpcClient(adapter);
        Assert.Equal(1, adapter.SubscribeCalls);

        client.Dispose();
        client.Dispose();

        Assert.Equal(1, adapter.UnsubscribeCalls);
        Assert.False(client.IsChangedSubscribed);
    }

    private static FakeQuartermasterIpcAdapter ReadyAdapter(string provider, long revision, string snapshotJson) => new()
    {
        CapabilitiesJson = CapabilitiesJson(provider, revision),
        SnapshotJson = snapshotJson,
    };

    private static string CapabilitiesJson(string provider, long revision) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.CapabilitiesSchema,
        providerInstanceId = provider,
        revision,
        generatedAtUtc = "2026-07-21T12:00:00Z",
    });

    private static string CapabilitiesJson(string provider, long revision, params string[] capabilities) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.CapabilitiesSchema,
        providerInstanceId = provider,
        revision,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        capabilities,
    });

    internal static string SnapshotJson(string provider, long revision, string retainerName) => JsonSerializer.Serialize(new
    {
        schema = QuartermasterIpcClient.SnapshotSchema,
        providerInstanceId = provider,
        revision,
        generatedAtUtc = "2026-07-21T12:00:00Z",
        owner = new
        {
            localContentId = 100UL,
            homeWorldId = 40U,
            characterName = "Wei Ning",
            homeWorldName = "Maduin",
        },
        playerStorage = new
        {
            requestedSources = new[] { "Inventory1", "Crystals" },
            observedSources = new[] { "Inventory1" },
        },
        retainers = new[]
        {
            new
            {
                retainerId = 10UL,
                retainerName,
                observedAtUtc = "2026-07-21T11:58:00Z",
                gil = 1234UL,
                gilObservedAtUtc = "2026-07-21T11:55:00Z",
                listingsObservedAtUtc = "2026-07-21T11:56:00Z",
                requestedSources = new[] { "RetainerPage1", "RetainerMarket" },
                observedSources = new[] { "RetainerPage1" },
                bags = new[]
                {
                    new
                    {
                        bagName = "RetainerInventory1",
                        location = "Retainer",
                        observedAtUtc = "2026-07-21T11:54:00Z",
                        items = new[]
                        {
                            new { itemId = 100U, itemName = "Elm Lumber", quantity = 25U, isHQ = false, condition = 0f },
                        },
                    },
                },
                listings = Array.Empty<object>(),
            },
        },
    });
}

internal sealed class FakeQuartermasterIpcAdapter : IQuartermasterIpcAdapter
{
    private Action<string>? changed;

    public bool HasCapabilities { get; set; } = true;
    public bool HasSnapshot { get; set; } = true;
    public bool HasSubmitShortages { get; set; } = true;
    public bool HasOperation { get; set; } = true;
    public string CapabilitiesJson { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public string AcknowledgementJson { get; set; } = string.Empty;
    public string OperationJson { get; set; } = string.Empty;
    public Func<string, string>? SubmitResponse { get; set; }
    public Exception? CapabilitiesException { get; set; }
    public Queue<string> CapabilitiesResponses { get; } = new();
    public List<string> SubmittedRequests { get; } = [];
    public int CapabilitiesCalls { get; private set; }
    public int SnapshotCalls { get; private set; }
    public int SubscribeCalls { get; private set; }
    public int UnsubscribeCalls { get; private set; }

    public string GetCapabilities()
    {
        CapabilitiesCalls++;
        if (CapabilitiesException is not null)
            throw CapabilitiesException;
        return CapabilitiesResponses.Count > 0 ? CapabilitiesResponses.Dequeue() : CapabilitiesJson;
    }

    public string GetSnapshot()
    {
        SnapshotCalls++;
        return SnapshotJson;
    }

    public string SubmitShortages(string requestJson)
    {
        SubmittedRequests.Add(requestJson);
        return SubmitResponse?.Invoke(requestJson) ?? AcknowledgementJson;
    }

    public string GetOperation(string operationId) => OperationJson;

    public void SubscribeChanged(Action<string> handler)
    {
        SubscribeCalls++;
        changed += handler;
    }

    public void UnsubscribeChanged(Action<string> handler)
    {
        UnsubscribeCalls++;
        changed -= handler;
    }

    public void RaiseChanged(string json) => changed?.Invoke(json);
}
