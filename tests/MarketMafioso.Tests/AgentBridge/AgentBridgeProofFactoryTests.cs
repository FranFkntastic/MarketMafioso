using MarketMafioso.AgentBridge;

namespace MarketMafioso.Tests.AgentBridge;

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
        MainWindowPinned = false,
        AcquisitionDiagnosticsOpen = false,
        WorkspaceStatus = "Ready",
        WorkspaceBusy = false,
        ClaimedRequestId = "request-1",
        PreparedPlanStatus = "Ready",
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
