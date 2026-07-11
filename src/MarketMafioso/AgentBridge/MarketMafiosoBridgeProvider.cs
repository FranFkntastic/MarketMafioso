using System;

namespace MarketMafioso.AgentBridge;

/// <summary>
/// Product-specific state and semantic actions exposed by MarketMafioso.
/// Transport, authentication, discovery, capture storage, and review orchestration do not belong here.
/// </summary>
public interface IMarketMafiosoBridgeProvider
{
    AgentBridgeTruth CreateSnapshot();
    void OpenMainWindow();
    void OpenAcquisitionDiagnostics();
    void OpenProof(string proofId);
    bool TrySelectMainTab(string tabName);
    void CaptureInputState();
    void StopRoute();
}

public sealed class MarketMafiosoBridgeProvider : IMarketMafiosoBridgeProvider
{
    private readonly Func<AgentBridgeTruth> createSnapshot;
    private readonly Action openMainWindow;
    private readonly Action openAcquisitionDiagnostics;
    private readonly Action<string> openProof;
    private readonly Func<string, bool> trySelectMainTab;
    private readonly Action captureInputState;
    private readonly Action stopRoute;

    public MarketMafiosoBridgeProvider(
        Func<AgentBridgeTruth> createSnapshot,
        Action openMainWindow,
        Action openAcquisitionDiagnostics,
        Action<string> openProof,
        Func<string, bool> trySelectMainTab,
        Action captureInputState,
        Action stopRoute)
    {
        this.createSnapshot = createSnapshot ?? throw new ArgumentNullException(nameof(createSnapshot));
        this.openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.openAcquisitionDiagnostics = openAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(openAcquisitionDiagnostics));
        this.openProof = openProof ?? throw new ArgumentNullException(nameof(openProof));
        this.trySelectMainTab = trySelectMainTab ?? throw new ArgumentNullException(nameof(trySelectMainTab));
        this.captureInputState = captureInputState ?? throw new ArgumentNullException(nameof(captureInputState));
        this.stopRoute = stopRoute ?? throw new ArgumentNullException(nameof(stopRoute));
    }

    public AgentBridgeTruth CreateSnapshot() => createSnapshot();
    public void OpenMainWindow() => openMainWindow();
    public void OpenAcquisitionDiagnostics() => openAcquisitionDiagnostics();
    public void OpenProof(string proofId) => openProof(proofId);
    public bool TrySelectMainTab(string tabName) => trySelectMainTab(tabName);
    public void CaptureInputState() => captureInputState();
    public void StopRoute() => stopRoute();
}

