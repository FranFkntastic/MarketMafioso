using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.WorkshopPrep;

// Read-only dump of the company crafting log window and its agent. It exists to
// find where the game keeps the FC's unlocked schematics: choosing a category or
// type the FC has not unlocked crashes the game's own handler, so MMF has to know
// the unlocked set before it sends anything. Nothing here dispatches to the game.
internal static unsafe class WorkshopCraftingLogInspector
{
    private const int AgentDumpBytes = 0x1000;
    private const int PointeeDumpBytes = 0x200;
    private const int MaxPointees = 48;
    private const int MaxNodeLines = 4000;
    private const int MaxNodeDepth = 8;

    public static string Describe(AtkUnitBase* addon, AgentInterface* agent)
    {
        var builder = new StringBuilder();
        DescribeAddon(builder, addon);
        builder.AppendLine();
        DescribeAgent(builder, agent);
        return builder.ToString();
    }

    private static void DescribeAddon(StringBuilder builder, AtkUnitBase* addon)
    {
        if (addon == null)
        {
            builder.AppendLine("addon: not loaded");
            return;
        }

        builder.AppendLine($"addon: id={addon->Id} visible={addon->IsVisible} ready={addon->IsReady} address={(nint)addon:X}");
        builder.AppendLine($"atkValues: count={addon->AtkValuesCount}");
        if (addon->AtkValues != null)
        {
            for (var index = 0; index < addon->AtkValuesCount; index++)
                builder.AppendLine($"  [{index}] {DescribeValue(addon->AtkValues[index])}");
        }

        builder.AppendLine("nodes:");
        var lines = 0;
        DescribeNodes(builder, &addon->UldManager, 1, ref lines);
        if (lines >= MaxNodeLines)
            builder.AppendLine("  (node listing truncated)");
    }

    private static string DescribeValue(AtkValue value)
    {
        try
        {
            return (value.Type & AtkValueType.TypeMask) switch
            {
                AtkValueType.Bool => $"Bool {value.Byte != 0}",
                AtkValueType.Int => $"Int {value.Int}",
                AtkValueType.UInt => $"UInt {value.UInt}",
                AtkValueType.Int64 => $"Int64 {value.Int64}",
                AtkValueType.UInt64 => $"UInt64 {value.UInt64}",
                AtkValueType.Float => $"Float {value.Float}",
                AtkValueType.String or AtkValueType.WideString or AtkValueType.String8 =>
                    $"{value.Type} \"{Escape(value.GetValueAsString())}\"",
                _ => $"{value.Type} raw={value.UInt64:X}",
            };
        }
        catch (Exception ex)
        {
            return $"{value.Type} unreadable ({ex.GetType().Name})";
        }
    }

    private static void DescribeNodes(StringBuilder builder, AtkUldManager* uld, int depth, ref int lines)
    {
        if (uld == null || uld->NodeList == null || depth > MaxNodeDepth)
            return;

        var indent = new string(' ', depth * 2);
        for (var index = 0; index < uld->NodeListCount && lines < MaxNodeLines; index++)
        {
            var node = uld->NodeList[index];
            if (node == null)
                continue;

            var visible = node->IsVisible() ? "v" : "h";
            if ((int)node->Type >= 1000)
            {
                builder.AppendLine($"{indent}#{node->NodeId} component type={(int)node->Type} {visible}");
                lines++;
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    DescribeNodes(builder, &component->UldManager, depth + 1, ref lines);
            }
            else if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                builder.AppendLine($"{indent}#{node->NodeId} text {visible} \"{Escape(text)}\"");
                lines++;
            }
        }
    }

    private static void DescribeAgent(StringBuilder builder, AgentInterface* agent)
    {
        if (agent == null)
        {
            builder.AppendLine("agent: unavailable");
            return;
        }

        builder.AppendLine($"agent: address={(nint)agent:X} addonId={agent->AddonId} active={agent->IsAgentActive()}");
        var bytes = SafeRead((nint)agent, AgentDumpBytes);
        if (bytes.Length == 0)
        {
            builder.AppendLine("agent memory: unreadable");
            return;
        }

        builder.AppendLine($"agent memory ({bytes.Length:X} bytes):");
        AppendHex(builder, bytes, "  ");

        var (moduleStart, moduleEnd) = MainModuleRange();
        var followed = new HashSet<nint>();
        for (var offset = 0; offset + 8 <= bytes.Length && followed.Count < MaxPointees; offset += 8)
        {
            var pointer = (nint)BitConverter.ToInt64(bytes, offset);
            if (!LooksLikeHeapPointer(pointer, moduleStart, moduleEnd) || !followed.Add(pointer))
                continue;

            var pointee = SafeRead(pointer, PointeeDumpBytes);
            if (pointee.Length == 0)
                continue;

            builder.AppendLine($"agent+0x{offset:X4} -> {pointer:X} ({pointee.Length:X} bytes):");
            AppendHex(builder, pointee, "    ");
        }
    }

    private static bool LooksLikeHeapPointer(nint value, nint moduleStart, nint moduleEnd) =>
        value > 0x10000 &&
        value < 0x7FFF_FFFF_FFFF &&
        (value & 0x7) == 0 &&
        (value < moduleStart || value >= moduleEnd);

    private static (nint Start, nint End) MainModuleRange()
    {
        try
        {
            var module = Process.GetCurrentProcess().MainModule;
            return module == null ? (0, 0) : (module.BaseAddress, module.BaseAddress + module.ModuleMemorySize);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static void AppendHex(StringBuilder builder, byte[] bytes, string indent)
    {
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var length = Math.Min(16, bytes.Length - offset);
            builder.Append(indent).Append($"+0x{offset:X4} ");
            for (var index = 0; index < 16; index++)
                builder.Append(index < length ? $"{bytes[offset + index]:X2} " : "   ");
            builder.Append("| u32:");
            for (var index = 0; index + 4 <= length; index += 4)
                builder.Append(' ').Append(BitConverter.ToUInt32(bytes, offset + index));
            builder.AppendLine();
        }
    }

    // ReadProcessMemory on our own process fails cleanly on unmapped pages, where
    // a raw pointer read would crash the game.
    private static byte[] SafeRead(nint address, int length)
    {
        var buffer = new byte[length];
        if (ReadProcessMemory(-1, address, buffer, length, out var read) && read == length)
            return buffer;

        var readable = new List<byte>();
        for (var chunk = 0; chunk < length; chunk += 0x100)
        {
            var part = new byte[0x100];
            if (!ReadProcessMemory(-1, address + chunk, part, part.Length, out read) || read != part.Length)
                break;
            readable.AddRange(part);
        }

        return readable.ToArray();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);
}
