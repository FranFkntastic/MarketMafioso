using System;
using System.Collections.Generic;

namespace MarketMafioso.AgentBridge;

public sealed class AgentBridgeProofStore
{
    private const int MaxRetainedProofs = 16;
    private readonly object sync = new();
    private readonly Dictionary<string, AgentBridgeProofReceipt> receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> retentionOrder = new();
    private AgentBridgeProofReceipt? current;

    public AgentBridgeProofReceipt Capture(AgentBridgeTruth truth, long revision, string? challenge)
    {
        lock (sync)
        {
            current = AgentBridgeProofFactory.Create(truth, revision, challenge);
            receipts[current.ProofId] = current;
            retentionOrder.Enqueue(current.ProofId);
            while (retentionOrder.Count > MaxRetainedProofs)
                receipts.Remove(retentionOrder.Dequeue());
            return current;
        }
    }

    public AgentBridgeProofReceipt? Get(string proofId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofId);
        lock (sync)
            return receipts.GetValueOrDefault(proofId);
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
            if (!receipts.TryGetValue(proofId, out var receipt))
                return;

            var presented = receipt with { PresentedInGame = true };
            receipts[proofId] = presented;
            if (string.Equals(current?.ProofId, proofId, StringComparison.Ordinal))
                current = presented;
        }
    }
}
