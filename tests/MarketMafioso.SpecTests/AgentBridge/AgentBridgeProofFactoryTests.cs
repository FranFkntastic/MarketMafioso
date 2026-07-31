using MarketMafioso.AgentBridge;

namespace MarketMafioso.SpecTests.AgentBridge;

public sealed class AgentBridgeProofFactoryTests
{
    [Fact]
    public void Create_UsesStableTruthHashAcrossProofMetadata()
    {
        var truth = CreateTruth();

        var first = AgentBridgeProofFactory.Create(truth, 1, "primary-instance", DateTimeOffset.UnixEpoch);
        var second = AgentBridgeProofFactory.Create(truth, 2, "fresh-challenge", DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Equal(first.TruthSha256, second.TruthSha256);
        Assert.NotEqual(first.ProofSha256, second.ProofSha256);
        Assert.NotEqual(first.ProofId, second.ProofId);
        Assert.Equal("primary-instance", first.Challenge);
    }

    [Fact]
    public void ProofStore_RetainsAndMarksExactProof()
    {
        var store = new AgentBridgeProofStore();
        var first = store.Capture(CreateTruth(), 1, "challenge-a");
        var second = store.Capture(CreateTruth(), 2, "challenge-b");

        store.MarkPresented("wrong-proof");
        Assert.False(store.GetCurrent()!.PresentedInGame);

        store.MarkPresented(first.ProofId);
        Assert.True(store.Get(first.ProofId)!.PresentedInGame);
        Assert.False(store.Get(second.ProofId)!.PresentedInGame);
        Assert.Equal(second.ProofId, store.GetCurrent()!.ProofId);
    }

    [Fact]
    public void Serialize_ExposesOnlyAggregatePersistedSunkStateForDryRunProof()
    {
        var receipt = AgentBridgeProofFactory.Create(CreateTruth(), 1);

        var json = AgentBridgeProofFactory.Serialize(receipt);

        Assert.Contains("\"persistedExactAcquisitionSunkReceiptCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"persistedExactAcquisitionSunkQuantity\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"persistedExactAcquisitionSunkGil\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"activeExactAcquisitionRemainingQuantity\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"activeExactAcquisitionRemainingGil\":100", json, StringComparison.Ordinal);
        Assert.DoesNotContain("listing-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ExposesObservableCraftAppraisalCompletion()
    {
        var receipt = AgentBridgeProofFactory.Create(CreateTruth(), 1);

        var json = AgentBridgeProofFactory.Serialize(receipt);

        Assert.Contains("\"craftAppraisal\":", json, StringComparison.Ordinal);
        Assert.Contains("\"isFetching\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Opened the quoted Craft Architect plan.\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quoteUnitCost\":8427", json, StringComparison.Ordinal);
        Assert.Contains("\"planId\":\"plan-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"canOpenPlan\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ExposesTradeQueueCheckpointAndExactNearbyRecipients()
    {
        var receipt = AgentBridgeProofFactory.Create(CreateTruth(), 1);

        var json = AgentBridgeProofFactory.Serialize(receipt);

        Assert.Contains("\"tradeQueue\":", json, StringComparison.Ordinal);
        Assert.Contains("\"runId\":\"trade-run-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"completedBatchCount\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"completedUnitCount\":5000", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Eriana Ning\"", json, StringComparison.Ordinal);
        Assert.Contains("\"homeWorld\":\"Siren\"", json, StringComparison.Ordinal);
        Assert.Contains("\"canReceiverReady\":true", json, StringComparison.Ordinal);
    }

    private static AgentBridgeTruth CreateTruth() => new()
    {
        SchemaVersion = 1,
        PluginInstanceId = "instance-1",
        ProcessId = 1234,
        PluginVersion = "1.0.0",
        CharacterName = "Tester",
        CurrentWorld = "Siren",
        HomeWorld = "Siren",
        MainWindowOpen = true,
        MainWindowCollapseOverrideActive = false,
        MainWindowPinned = false,
        AcquisitionDiagnosticsOpen = false,
        WorkspaceStatus = "Ready",
        WorkspaceBusy = false,
        ClaimedRequestId = "request-1",
        PreparedPlanStatus = "Ready",
        TradeQueue = new AgentBridgeTradeQueueTruth
        {
            State = "WaitingForPartner",
            Message = "Batch 3 is loaded.",
            RunId = "trade-run-1",
            IsActive = true,
            CanResume = false,
            PartnerName = "Eriana Ning",
            BatchNumber = 3,
            CompletedBatchCount = 2,
            InitialUnitCount = 10_500,
            CompletedUnitCount = 5_000,
            RemainingLineCount = 12,
            RemainingUnitCount = 5_500,
            QueueValid = true,
            QueueValidationMessage = "Trade Queue is ready.",
            ActionDelayMilliseconds = 50,
            TradeRetryMilliseconds = 1_000,
            AutoAcceptIncomingTrades = true,
            IsTradeOpen = true,
            OfferedSlotCount = 5,
            CanReceiverReady = true,
            CanReceiverConfirm = false,
            CanReceiverCancel = true,
            AvailablePartners =
            [
                new AgentBridgeTradePartnerTruth
                {
                    Name = "Eriana Ning",
                    HomeWorld = "Siren",
                    GameObjectId = "40001234",
                },
            ],
            Queue =
            [
                new AgentBridgeTradeQueueLineTruth
                {
                    ItemId = 5074,
                    ItemName = "Cobalt Plate",
                    Quantity = 840,
                },
            ],
        },
        CraftAppraisal = new AgentBridgeCraftAppraisalTruth
        {
            IsFetching = false,
            Status = "Opened the quoted Craft Architect plan.",
            WorkshopHostEnabled = true,
            WorkshopHostAvailable = true,
            SelectedItemId = 11953,
            SelectedItemName = "Adamantite Scythe",
            RequestedQuantity = 1,
            HqPolicy = "HQOnly",
            Region = "North America",
            HasQuote = true,
            QuoteComplete = true,
            QuoteUnitCost = 8427m,
            QuoteSource = "CraftArchitectHosted",
            QuoteConfidence = "Medium",
            WarningCount = 16,
            PlanId = "plan-1",
            CanOpenPlan = true,
        },
        RemoteMarket = new AgentBridgeRemoteMarketTruth
        {
            Available = true,
            ResultVisible = true,
            ViewRevision = 7,
            ListingCount = 10,
            ItemId = 8,
            HighQuality = false,
            CheapestUnitPrice = 50,
            CurrentGil = 1_000,
            MarketContextSource = "Universalis",
            MarketContextSummary = "DC best 45p",
        },
        RemoteBellProbe = new AgentBridgeRemoteBellProbeTruth
        {
            Active = false,
            CanSubmit = true,
            State = "Idle",
            Message = "Ready",
            Readiness = "Loaded bell is out of range.",
            BellGameObjectId = "100519898",
            Distance = 27.7f,
            OrdinaryInteractionDistance = 4.75f,
            LastEvidencePath = null,
        },
        Route = new AgentBridgeRouteTruth
        {
            State = "Running",
            StatusMessage = "Waiting for arrival.",
            VisibleStatus = "Waiting for arrival.",
            IsActive = true,
            IsRunning = true,
            IsPaused = false,
            ActiveWorld = "Maduin",
            ActiveStopStatus = "TravelCommandSent",
            ActiveOperationId = "op-1",
            ActiveOperationKind = "Travel",
            ActiveOperationPhase = "Waiting",
            ActiveOperationDisposition = "Pending",
            StopCount = 2,
            CompletedOrProbedStopCount = 1,
            PersistedExactAcquisitionSunkReceiptCount = 1,
            PersistedExactAcquisitionSunkQuantity = 1,
            PersistedExactAcquisitionSunkGil = 100,
            ActiveExactAcquisitionRemainingQuantity = 1,
            ActiveExactAcquisitionRemainingGil = 100,
        },
    };
}
