using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketMafioso.Quartermaster;
using MarketMafioso.Tests.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopQuartermasterRequestServiceTests
{
    [Fact]
    public void Submit_UsesOwnerScopeItemNamesAndStableInternalIdsIdempotently()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 7,
                capabilities = new[] { QuartermasterIpcClient.AutomaticRetrievalCapability },
            }),
        };
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration();
        var saveCount = 0;
        using var service = new WorkshopQuartermasterRequestService(
            config,
            client,
            () => saveCount++,
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var availability = new[]
        {
            new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, []),
        };

        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 7,
                accepted = true,
                status = "Queued",
            });
        };
        Assert.True(service.Submit(owner, availability));
        config.QuartermasterWorkshopRequests["100:40"].Status = "running";
        config.QuartermasterWorkshopRequests["100:40"].Revision = 8;
        Assert.True(service.Submit(owner, availability));

        Assert.Equal(2, adapter.SubmittedRequests.Count);
        using var first = JsonDocument.Parse(adapter.SubmittedRequests[0]);
        using var second = JsonDocument.Parse(adapter.SubmittedRequests[1]);
        Assert.Equal(QuartermasterIpcClient.ShortageRequestSchema, first.RootElement.GetProperty("schema").GetString());
        Assert.Equal("provider-a", first.RootElement.GetProperty("providerInstanceId").GetString());
        Assert.Equal(first.RootElement.GetProperty("requestId").GetString(), second.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(first.RootElement.GetProperty("operationId").GetString(), second.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(first.RootElement.GetProperty("submittedAtUtc").GetString(), second.RootElement.GetProperty("submittedAtUtc").GetString());
        Assert.True(first.RootElement.GetProperty("executeImmediately").GetBoolean());
        Assert.True(second.RootElement.GetProperty("executeImmediately").GetBoolean());
        Assert.Equal(100UL, first.RootElement.GetProperty("owner").GetProperty("localContentId").GetUInt64());
        Assert.Equal(40U, first.RootElement.GetProperty("owner").GetProperty("homeWorldId").GetUInt32());
        Assert.Equal("Wei Ning", first.RootElement.GetProperty("owner").GetProperty("characterName").GetString());
        var target = Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(100U, target.GetProperty("itemId").GetUInt32());
        Assert.Equal("Elm Lumber", target.GetProperty("itemName").GetString());
        Assert.Equal(50, target.GetProperty("targetQuantity").GetInt32());
        Assert.Equal(30, target.GetProperty("shortageQuantity").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("100|40|1|100:Elm Lumber:50:30"))),
            config.QuartermasterWorkshopRequests["100:40"].Signature);
        Assert.Equal(3, saveCount);
        Assert.Equal("running", config.QuartermasterWorkshopRequests["100:40"].Status);
        Assert.Equal(8, config.QuartermasterWorkshopRequests["100:40"].Revision);
        Assert.Equal("Immediate Quartermaster retrieval requested. Waiting for terminal operation status.", service.LastStatus);
    }

    [Fact]
    public void Submit_WhenAutomaticRetrievalBecomesAvailable_ReplacesReviewOnlyIdentity()
    {
        var reviewCapabilities = JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.CapabilitiesSchema,
            providerInstanceId = "provider-a",
            revision = 7,
        });
        var automaticCapabilities = JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.CapabilitiesSchema,
            providerInstanceId = "provider-a",
            revision = 8,
            capabilities = new[] { QuartermasterIpcClient.AutomaticRetrievalCapability },
        });
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = automaticCapabilities,
        };
        adapter.CapabilitiesResponses.Enqueue(reviewCapabilities);
        adapter.CapabilitiesResponses.Enqueue(automaticCapabilities);
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 7,
                status = "accepted",
            });
        };
        var config = new Configuration();
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var availability = new[] { new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, []) };

        Assert.True(service.Submit(owner, availability));

        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        Assert.False(submitted.RootElement.TryGetProperty("executeImmediately", out _));
        Assert.False(service.LastAcknowledgement!.ExecuteImmediately);
        Assert.Equal("Quartermaster accepted shortages for review; automatic retrieval is unavailable.", service.LastStatus);

        Assert.True(service.Submit(owner, availability));

        using var automatic = JsonDocument.Parse(adapter.SubmittedRequests[1]);
        Assert.NotEqual(submitted.RootElement.GetProperty("requestId").GetString(), automatic.RootElement.GetProperty("requestId").GetString());
        Assert.NotEqual(submitted.RootElement.GetProperty("operationId").GetString(), automatic.RootElement.GetProperty("operationId").GetString());
        Assert.True(automatic.RootElement.GetProperty("executeImmediately").GetBoolean());
        Assert.True(service.LastAcknowledgement!.ExecuteImmediately);
        Assert.Equal(2, adapter.CapabilitiesCalls);
    }

    [Fact]
    public void Submit_ReplayingActiveOperationPreservesRevisionUntilPolled()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 9,
            }),
        };
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = adapter.SubmittedRequests.Count == 1 ? 7 : 9,
                status = adapter.SubmittedRequests.Count == 1 ? "accepted" : "queued",
                message = adapter.SubmittedRequests.Count == 1 ? "Accepted." : "Replay acknowledged.",
            });
        };
        var config = new Configuration();
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var availability = new[] { new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, []) };
        Assert.True(service.Submit(owner, availability));
        var persisted = config.QuartermasterWorkshopRequests["100:40"];
        persisted.Status = "running";
        persisted.Message = "Retrieving stock.";
        persisted.Revision = 8;
        adapter.OperationJson = JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.OperationSchema,
            providerInstanceId = "provider-a",
            operationId = persisted.OperationId,
            requestId = persisted.RequestId,
            owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
            status = "running",
            message = "Retrieving stock.",
            revision = 8,
        });

        Assert.True(service.Submit(owner, availability));

        var replayed = config.QuartermasterWorkshopRequests["100:40"];
        Assert.Equal(8, replayed.Revision);
        Assert.Equal("running", replayed.Status);
        Assert.Equal("Retrieving stock.", replayed.Message);
        service.PollOperationIfDue(owner);
        Assert.Equal(8, service.LastOperation!.Revision);
    }

    [Fact]
    public void Submit_WhenIdentityPersistenceFails_RollsBackAndDoesNotSend()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 7,
            }),
        };
        using var client = new QuartermasterIpcClient(adapter);
        var config = new Configuration();
        var saveAttempts = 0;
        using var service = new WorkshopQuartermasterRequestService(
            config,
            client,
            () =>
            {
                saveAttempts++;
                throw new InvalidOperationException("disk unavailable");
            },
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var availability = new[]
        {
            new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, []),
        };

        Assert.False(service.Submit(new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin"), availability));

        Assert.Equal(1, saveAttempts);
        Assert.Empty(adapter.SubmittedRequests);
        Assert.Empty(config.QuartermasterWorkshopRequests);
    }

    [Fact]
    public void Submit_AfterUnknownAcceptanceRejectsChangedShortagesAndPreservesIdentity()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 7,
                capabilities = new[] { QuartermasterIpcClient.AutomaticRetrievalCapability },
            }),
            SubmitResponse = _ => throw new IOException("IPC response lost"),
        };
        var config = new Configuration();
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");

        Assert.False(service.Submit(
            owner,
            [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, [])]));
        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        Assert.True(submitted.RootElement.GetProperty("executeImmediately").GetBoolean());
        var persisted = config.QuartermasterWorkshopRequests["100:40"];
        var requestId = persisted.RequestId;
        var operationId = persisted.OperationId;
        adapter.SubmitResponse = _ => throw new InvalidOperationException("must not resubmit");

        Assert.False(service.Submit(
            owner,
            [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 60, 20, 25, 40, 5, [])]));

        var retained = config.QuartermasterWorkshopRequests["100:40"];
        Assert.Single(adapter.SubmittedRequests);
        Assert.Equal(requestId, retained.RequestId);
        Assert.Equal(operationId, retained.OperationId);
        Assert.Contains("uncertain state", service.LastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_NewRequestClearsPreviousTerminalOperation()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 7,
            }),
            OperationJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.OperationSchema,
                providerInstanceId = "provider-a",
                operationId = "old-operation",
                requestId = "old-request",
                owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                status = "failed",
                revision = 6,
            }),
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100,
            HomeWorldId = 40,
            CharacterName = "Wei Ning",
            Signature = "old-signature",
            RequestId = "old-request",
            OperationId = "old-operation",
            SubmittedAtUtc = new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Utc),
            ProviderInstanceId = "provider-a",
            Status = "accepted",
        };
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        service.PollOperationIfDue(owner);
        Assert.True(service.LastOperation!.IsTerminal);
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 7,
                status = "queued",
            });
        };

        var submitted = service.Submit(
            owner,
            [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, [])]);

        Assert.True(submitted);
        Assert.Null(service.LastOperation);
    }

    [Fact]
    public void Submit_ReplacesLegacyNonterminalReviewRequestForAutomaticRetrieval()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 7,
                capabilities = new[] { QuartermasterIpcClient.AutomaticRetrievalCapability },
            }),
        };
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 7,
                status = "accepted",
            });
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100,
            HomeWorldId = 40,
            CharacterName = "Wei Ning",
            HomeWorldName = "Maduin",
            Signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("100|40|100:Elm Lumber:50:30"))),
            RequestId = "legacy-request",
            OperationId = "legacy-operation",
            SubmittedAtUtc = new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Utc),
            Status = "accepted",
        };
        var saveCount = 0;
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(
            config,
            client,
            () => saveCount++,
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");

        Assert.True(service.Submit(
            owner,
            [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, [])]));

        using var submitted = JsonDocument.Parse(Assert.Single(adapter.SubmittedRequests));
        Assert.NotEqual("legacy-request", submitted.RootElement.GetProperty("requestId").GetString());
        Assert.NotEqual("legacy-operation", submitted.RootElement.GetProperty("operationId").GetString());
        Assert.True(submitted.RootElement.GetProperty("executeImmediately").GetBoolean());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("100|40|1|100:Elm Lumber:50:30"))),
            config.QuartermasterWorkshopRequests["100:40"].Signature);
        Assert.Equal(2, saveCount);
    }

    [Fact]
    public void Poll_PersistsImmutableReceiptsInsideOwnerScope()
    {
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-a",
                revision = 8,
            }),
            OperationJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.OperationSchema,
                providerInstanceId = "provider-a",
                operationId = "operation-1",
                requestId = "request-1",
                owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                status = "running",
                revision = 2,
                updatedAtUtc = "2026-07-21T12:00:00Z",
                receipts = new[]
                {
                    new
                    {
                        revision = 1L,
                        occurredAtUtc = "2026-07-21T11:59:00Z",
                        status = "accepted",
                        code = "ShortagesAccepted",
                        message = "Accepted for review.",
                    },
                },
            }),
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100,
            HomeWorldId = 40,
            CharacterName = "Wei Ning",
            Signature = "signature",
            RequestId = "request-1",
            OperationId = "operation-1",
            SubmittedAtUtc = new DateTime(2026, 7, 21, 11, 58, 0, DateTimeKind.Utc),
            Status = "accepted",
        };
        var saves = 0;
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => saves++);

        service.PollOperationIfDue(owner);

        var state = config.QuartermasterWorkshopRequests["100:40"];
        var receipt = Assert.Single(state.Receipts);
        Assert.Equal("ShortagesAccepted", receipt.Code);
        Assert.Equal("running", state.Status);
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("completed")]
    public void Submit_AfterTerminalOperationCreatesFreshIdentityForSameShortages(string terminalStatus)
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new { schema = QuartermasterIpcClient.CapabilitiesSchema, providerInstanceId = "provider-a", revision = 1 }),
        };
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 1,
                status = "accepted",
            });
        };
        var config = new Configuration();
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var availability = new[] { new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, []) };

        Assert.True(service.Submit(owner, availability));
        config.QuartermasterWorkshopRequests["100:40"].Status = terminalStatus;
        Assert.True(service.Submit(owner, availability));

        using var first = JsonDocument.Parse(adapter.SubmittedRequests[0]);
        using var second = JsonDocument.Parse(adapter.SubmittedRequests[1]);
        Assert.NotEqual(first.RootElement.GetProperty("requestId").GetString(), second.RootElement.GetProperty("requestId").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("operationId").GetString(), second.RootElement.GetProperty("operationId").GetString());
    }

    [Fact]
    public void Submit_DoesNotReplaceActiveOperationWhenShortagesChange()
    {
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new { schema = QuartermasterIpcClient.CapabilitiesSchema, providerInstanceId = "provider-a", revision = 1 }),
        };
        adapter.SubmitResponse = requestJson =>
        {
            using var request = JsonDocument.Parse(requestJson);
            return JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.AcknowledgementSchema,
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                operationId = request.RootElement.GetProperty("operationId").GetString(),
                providerInstanceId = "provider-a",
                revision = 1,
                status = "accepted",
            });
        };
        var config = new Configuration();
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { });
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        Assert.True(service.Submit(owner, [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 50, 20, 25, 30, 5, [])]));
        var operationId = config.QuartermasterWorkshopRequests["100:40"].OperationId;

        Assert.False(service.Submit(owner, [new WorkshopMaterialAvailability(100, "Elm Lumber", 1, 60, 20, 25, 40, 5, [])]));

        Assert.Single(adapter.SubmittedRequests);
        Assert.Equal(operationId, config.QuartermasterWorkshopRequests["100:40"].OperationId);
        Assert.Contains("still accepted", service.LastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poll_RejectsOperationRevisionRegression()
    {
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new { schema = QuartermasterIpcClient.CapabilitiesSchema, providerInstanceId = "provider-a", revision = 1 }),
            OperationJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.OperationSchema,
                providerInstanceId = "provider-a",
                operationId = "operation-1",
                requestId = "request-1",
                owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                status = "running",
                revision = 4,
            }),
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100, HomeWorldId = 40, CharacterName = "Wei Ning", RequestId = "request-1", OperationId = "operation-1",
            ProviderInstanceId = "provider-a", Revision = 5, Status = "accepted",
        };
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => throw new InvalidOperationException("must not save"));

        service.PollOperationIfDue(owner);

        Assert.Equal(5, config.QuartermasterWorkshopRequests["100:40"].Revision);
        Assert.Equal("accepted", config.QuartermasterWorkshopRequests["100:40"].Status);
        Assert.Contains("regressed", service.LastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poll_RejectsNewReceiptAtPersistedOperationRevision()
    {
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new { schema = QuartermasterIpcClient.CapabilitiesSchema, providerInstanceId = "provider-a", revision = 2 }),
            OperationJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.OperationSchema,
                providerInstanceId = "provider-a",
                operationId = "operation-1",
                requestId = "request-1",
                owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                status = "running",
                revision = 2,
                receipts = new[]
                {
                    new
                    {
                        revision = 2L,
                        occurredAtUtc = "2026-07-21T12:00:00Z",
                        status = "running",
                        code = "RetainerOpened",
                        message = "Retainer opened.",
                    },
                },
            }),
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100,
            HomeWorldId = 40,
            CharacterName = "Wei Ning",
            RequestId = "request-1",
            OperationId = "operation-1",
            Revision = 2,
            Status = "running",
        };
        var saves = 0;
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => saves++);

        service.PollOperationIfDue(owner);

        Assert.Empty(config.QuartermasterWorkshopRequests["100:40"].Receipts);
        Assert.Equal(0, saves);
        Assert.Null(service.LastOperation);
        Assert.Contains("without advancing", service.LastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poll_WhenSaveFailsRestoresRetryableStateAndThenPersists()
    {
        var owner = new QuartermasterOwnerScope(100, 40, "Wei Ning", "Maduin");
        var adapter = new FakeQuartermasterIpcAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new { schema = QuartermasterIpcClient.CapabilitiesSchema, providerInstanceId = "provider-a", revision = 1 }),
            OperationJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.OperationSchema,
                providerInstanceId = "provider-a",
                operationId = "operation-1",
                requestId = "request-1",
                owner = new { localContentId = 100UL, homeWorldId = 40U, characterName = "Wei Ning", homeWorldName = "Maduin" },
                status = "succeeded",
                revision = 2,
                receipts = new[] { new { revision = 2L, occurredAtUtc = "2026-07-21T12:00:00Z", status = "succeeded", code = "done", message = "done" } },
            }),
        };
        var config = new Configuration();
        config.QuartermasterWorkshopRequests["100:40"] = new QuartermasterWorkshopRequestState
        {
            LocalContentId = 100, HomeWorldId = 40, CharacterName = "Wei Ning", RequestId = "request-1", OperationId = "operation-1", Status = "accepted",
        };
        var attempts = 0;
        using var client = new QuartermasterIpcClient(adapter);
        using var service = new WorkshopQuartermasterRequestService(config, client, () => { if (++attempts == 1) throw new IOException("disk unavailable"); });

        service.PollOperationIfDue(owner);
        Assert.Equal("accepted", config.QuartermasterWorkshopRequests["100:40"].Status);
        Assert.Empty(config.QuartermasterWorkshopRequests["100:40"].Receipts);
        Assert.Null(service.LastOperation);

        service.PollOperationIfDue(owner);
        Assert.Equal("succeeded", config.QuartermasterWorkshopRequests["100:40"].Status);
        Assert.Single(config.QuartermasterWorkshopRequests["100:40"].Receipts);
        Assert.NotNull(service.LastOperation);
    }
}
