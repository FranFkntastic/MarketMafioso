using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.WorkshopPrep;

internal static class WorkshopCallbackProtocol
{
    internal static void RequirePayload(WorkshopCallbackOperation operation, ReadOnlySpan<AtkValue> values)
    {
        var count = operation is WorkshopCallbackOperation.Project or WorkshopCallbackOperation.Category ? 8 :
            operation == WorkshopCallbackOperation.Contribution ? 4 : 1;
        var command = operation switch { WorkshopCallbackOperation.Project => 1, WorkshopCallbackOperation.Category => 2,
            WorkshopCallbackOperation.Contribution => 0, WorkshopCallbackOperation.Close => -1,
            _ => throw new InvalidOperationException("Unknown workshop callback operation.") };
        if (values.Length != count || values[0].Type != AtkValueType.Int || values[0].Int != command)
            throw UnsupportedPayload();
        for (var index = 1; index < count; index++)
        {
            var unsigned = operation switch
            {
                WorkshopCallbackOperation.Project => index == 4,
                WorkshopCallbackOperation.Category => index is >= 2 and <= 6,
                WorkshopCallbackOperation.Contribution => index is 1 or 2,
                _ => false,
            };
            if (values[index].Type != (unsigned ? AtkValueType.UInt : 0) || (!unsigned && values[index].UInt != 0))
                throw UnsupportedPayload();
        }
        if (operation == WorkshopCallbackOperation.Project && values[4].UInt == 0 ||
            operation == WorkshopCallbackOperation.Category && (values[2].UInt == 0 || values[3].UInt == 0 ||
                values[4].UInt != 0 || values[5].UInt != 0 || values[6].UInt != 0) ||
            operation == WorkshopCallbackOperation.Contribution && values[2].UInt == 0)
            throw UnsupportedPayload();
    }

    internal static void RequireOwner(nint capturedAddon, nint currentAddon, uint addonId, uint agentAddonId,
        nint receiver, nint expectedReceiver)
    {
        if (capturedAddon == 0 || capturedAddon != currentAddon || addonId == 0 || addonId != agentAddonId ||
            receiver == 0 || receiver != expectedReceiver)
            throw new InvalidOperationException("Workshop callback ownership or native handler changed; no callback was sent.");
    }

    internal static void RequireCraftingLogSchema(ReadOnlySpan<AtkValue> values)
    {
        RequireUInt(values, 13);
        var count = values[13].UInt;
        if (count > (values.Length - 14) / 4)
            throw UnsupportedSchema();
        var ids = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var offset = 14 + index * 4;
            RequireUInt(values, offset);
            RequireString(values, offset + 3);
            if (values[offset].UInt == 0 || !ids.Add(values[offset].UInt))
                throw UnsupportedSchema();
        }
    }

    internal static void RequireProjectVisible(IReadOnlyList<WorkshopCraftingLogItem> items, uint projectId)
    {
        var matches = 0;
        foreach (var item in items)
            if (item.WorkshopItemId == projectId)
                matches++;
        if (projectId == 0 || matches != 1)
            throw new InvalidOperationException("The requested workshop project is no longer uniquely visible; no callback was sent.");
    }

    internal static void RequireMaterialSchema(ReadOnlySpan<AtkValue> values, int nativeSupplyCapacity)
    {
        RequireUInt(values, 0);
        RequireUInt(values, 6);
        RequireUInt(values, 7);
        RequireUInt(values, 11);
        var count = values[11].UInt;
        if (values[0].UInt == 0 || values[7].UInt == 0 || values[6].UInt > values[7].UInt ||
            count == 0 || count > nativeSupplyCapacity || count > 24 || values.Length < 132 + count)
            throw UnsupportedSchema();
        for (var index = 0; index < count; index++)
        {
            RequireUInt(values, 12 + index);
            RequireString(values, 36 + index);
            RequireUInt(values, 60 + index);
            RequireUInt(values, 108 + index);
            RequireUInt(values, 120 + index);
            RequireUInt(values, 132 + index);
            if (values[12 + index].UInt == 0 || values[60 + index].UInt == 0 ||
                values[120 + index].UInt == 0 || values[108 + index].UInt > values[120 + index].UInt ||
                values[132 + index].UInt > 1)
                throw UnsupportedSchema();
        }
    }

    internal static void RequireContribution(WorkshopCraftState captured, WorkshopCraftState current,
        int index, WorkshopCraftMaterialState requested, byte nativeMode, bool requestActive,
        uint nativeResultItem, IReadOnlyList<uint> nativeSupplyItems, IReadOnlyList<byte> nativeQuantities)
    {
        if (nativeMode != 1 || requestActive || current.ResultItem != captured.ResultItem ||
            nativeResultItem != current.ResultItem || current.StepsComplete != captured.StepsComplete ||
            current.StepsTotal != captured.StepsTotal || index < 0 || index >= current.Items.Count ||
            index >= captured.Items.Count || index >= nativeSupplyItems.Count || index >= nativeQuantities.Count ||
            captured.Items[index] != requested || current.Items[index] != requested ||
            nativeSupplyItems[index] != requested.ItemId || nativeQuantities[index] != requested.ItemCountPerStep ||
            requested.Finished || requested.StepsComplete >= requested.StepsTotal)
            throw new InvalidOperationException("Workshop material, quantity, phase or native request state changed; no contribution was sent.");
    }

    private static void RequireUInt(ReadOnlySpan<AtkValue> values, int index)
    {
        if (index < 0 || index >= values.Length || values[index].Type != AtkValueType.UInt)
            throw UnsupportedSchema();
    }

    private static unsafe void RequireString(ReadOnlySpan<AtkValue> values, int index)
    {
        if (index < 0 || index >= values.Length ||
            values[index].Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) ||
            (byte*)values[index].String == null)
            throw UnsupportedSchema();
    }

    private static InvalidOperationException UnsupportedSchema() =>
        new("Workshop values do not match the typed recipe/material callback schema; no callback was sent.");
    private static InvalidOperationException UnsupportedPayload() =>
        new("Workshop callback arguments do not match the selected operation; no callback was sent.");
}

internal enum WorkshopCallbackOperation { Project, Category, Contribution, Close }
