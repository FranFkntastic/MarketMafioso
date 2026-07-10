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
        Assert.NotEqual(first.ProofId, second.ProofId);
        Assert.Equal("primary-instance", first.Challenge);
    }

    [Fact]
    public void ProofStore_MarksOnlyCurrentProofAsPresented()
    {
        var store = new AgentBridgeProofStore();
        var receipt = store.Capture(CreateTruth(), 1, "challenge");

        store.MarkPresented("wrong-proof");
        Assert.False(store.GetCurrent()!.PresentedInGame);

        store.MarkPresented(receipt.ProofId);
        Assert.True(store.GetCurrent()!.PresentedInGame);
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
        },
    };
}
