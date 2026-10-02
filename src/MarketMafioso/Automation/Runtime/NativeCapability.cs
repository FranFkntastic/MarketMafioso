using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Automation.Runtime;

internal static class NativeCapability
{
    public const string FailureCode = "NativeCapabilityUnavailable";

    public static void RequireAddress(nint address, string name)
    {
        if (address == 0)
            throw new InvalidOperationException($"{name} native address could not be resolved.");
    }

    public static nint ResolveUnique(ISigScanner scanner, string signature, string name)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException($"{name} requires the Windows x64 native calling convention.");
        var address = ResolveUnique(scanner.ScanAllText(signature), name);
        var textStart = scanner.Module.BaseAddress + checked((nint)scanner.TextSectionOffset);
        if (address < textStart || address - textStart >= scanner.TextSectionSize)
            throw new InvalidOperationException($"{name} signature resolved outside the game's executable code section.");
        return address;
    }

    internal static nint ResolveUnique(IReadOnlyList<nint> matches, string name)
    {
        if (matches.Count != 1 || matches[0] == 0)
            throw new InvalidOperationException($"{name} signature resolved {matches.Count} matches; exactly one nonzero native address is required.");
        return matches[0];
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
