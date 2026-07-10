using System;

namespace MarketMafioso.AgentBridge;

public sealed class AgentBridgeProofStore
{
    private readonly object sync = new();
    private AgentBridgeProofReceipt? current;

    public AgentBridgeProofReceipt Capture(AgentBridgeTruth truth, long revision, string? challenge)
    {
        lock (sync)
        {
            current = AgentBridgeProofFactory.Create(truth, revision, challenge);
            return current;
        }
    }

    public AgentBridgeProofReceipt? GetCurrent()
    {
        lock (sync)
            return current;
    }

    public void MarkPresented(string proofId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofId);
        lock (sync)
        {
            if (current is not { } receipt || !string.Equals(receipt.ProofId, proofId, StringComparison.Ordinal))
                return;

            current = receipt with { PresentedInGame = true };
        }
    }
}
