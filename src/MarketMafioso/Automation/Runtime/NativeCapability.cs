using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Diagnostics;

namespace MarketMafioso.Automation.Runtime;

internal static class NativeCapability
{
    public const string FailureCode = NativeCapabilityGuard.FailureCode;

    public static void RequireAddress(nint address, string name)
    {
        NativeCapabilityGuard.RequireAddress(address, name);
    }

    public static nint ResolveUnique(ISigScanner scanner, string signature, string name)
    {
        return NativeCapabilityGuard.ResolveUnique(scanner, signature, name);
    }

    internal static nint ResolveUnique(IReadOnlyList<nint> matches, string name)
    {
        return NativeCapabilityGuard.ResolveUnique(matches, name);
    }

    internal static void RequireTradeContextLayout(int agentSize, int contextOffset, int contextSize)
    {
        // ClientStructs exposes the InventoryContextEvent base, while the trade
        // specialization extends it inside AgentTrade. Validate the enclosing
        // allocation through +0x55C; the signature binds the +0x558 access.
        if (contextOffset < 0 || contextSize < IntPtr.Size ||
            agentSize - contextOffset < Math.Max(contextSize, 0x560))
            throw new InvalidOperationException("AgentTrade.InventoryContextEvent does not contain the native offer-item fields; the trade layout is unsupported.");
    }
}
