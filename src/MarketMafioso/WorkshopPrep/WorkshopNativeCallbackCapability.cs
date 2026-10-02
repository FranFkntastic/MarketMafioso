using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.WorkshopPrep;

internal sealed class WorkshopNativeCallbackCapability(ISigScanner scanner)
{
    // Bind callback dispatch and the argument accesses, not a client version or
    // a fixed RVA. Relocations and call displacements remain wildcarded.
    internal const string RecipeReceiverSignature = "40 55 56 57 41 56 B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 49 8B E8 8B 8C 24 ?? ?? ?? ?? 4C 8B F2 40 B6 01 85 C9";
    internal const string RecipeDispatchSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 54 41 56 41 57 B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B D8 48 8B CA 48 8B EA 45 32 F6 E8 ?? ?? ?? ?? FF C8 83 F8 06";
    internal const string RecipeDispatchCallSignature = "45 8B C1 48 8B D5 48 8B CF E8 ?? ?? ?? ?? 0F B6 F0 41 C7 06 02 00 00 00";
    internal const string ProjectArgumentSignature = "48 8D 4D 40 E8 ?? ?? ?? ?? 89 47 40 44 8B C8 4C 8B 57 48";
    internal const string ProjectRouteSignature = "48 83 7F 28 00 0F 84 ?? ?? ?? ?? 85 DB 0F 8E ?? ?? ?? ?? BE ?? ?? ?? ?? 48 8D 44 24 ?? 8B CE 33 DB";
    internal const string CategoryArgumentSignature = "48 8D 4D 20 E8 ?? ?? ?? ?? 48 8D 4D 30 8B D8 E8 ?? ?? ?? ?? 44 8B C0 8B D3 48 8B CF E8 ?? ?? ?? ?? 83 4F 30 01";
    internal const string RecipeSwitchSignature = "FF C8 83 F8 06 0F 87 ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ?? 48 98 8B 8C 82 ?? ?? ?? ?? 48 03 CA FF E1";
    internal const string MaterialReceiverSignature = "40 55 53 56 57 41 54 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 33 C0 48 8B F9 0F B6 89 98 00 00 00 0F 57 C0";
    internal const string MaterialRowSignature = "48 8D 4B 10 E8 ?? ?? ?? ?? 44 0F B6 C0 88 87 99 00 00 00 C6 44 24 ?? 01 42 8B 8C 87 9C 00 00 00";
    internal const string MaterialQuantitySignature = "41 0F B6 84 38 CC 00 00 00";
    internal const string MaterialRequestActiveSignature = "85 C0 75 ?? 44 38 BF E4 00 00 00 0F 85 ?? ?? ?? ?? 48 8D 4B 10";
    private nint recipeReceiver;
    private nint materialReceiver;

    internal nint RequireRecipe()
    {
        if (recipeReceiver != 0)
            return recipeReceiver;
        var receiver = NativeCapability.ResolveUnique(scanner, RecipeReceiverSignature, "Workshop recipe receiver");
        var dispatch = NativeCapability.ResolveUnique(scanner, RecipeDispatchSignature, "Workshop recipe callback dispatch");
        var call = NativeCapability.ResolveUnique(scanner, RecipeDispatchCallSignature, "Workshop recipe dispatch call");
        RequireWithin(receiver, call, 0x400);
        var target = call + 14 + Marshal.ReadInt32(call + 10);
        if (target != dispatch)
            throw new InvalidOperationException("Workshop recipe receiver no longer calls the validated argument dispatcher.");
        var project = NativeCapability.ResolveUnique(scanner, ProjectArgumentSignature, "Workshop project argument");
        var category = NativeCapability.ResolveUnique(scanner, CategoryArgumentSignature, "Workshop category arguments");
        var projectRoute = NativeCapability.ResolveUnique(scanner, ProjectRouteSignature, "Workshop project command route");
        RequireWithin(dispatch, project, 0x600);
        RequireWithin(dispatch, category, 0x600);
        var branch = NativeCapability.ResolveUnique(scanner, RecipeSwitchSignature, "Workshop operation dispatch table");
        RequireWithin(dispatch, branch, 0x100);
        var imageBase = branch + 18 + Marshal.ReadInt32(branch + 14);
        if (imageBase != scanner.Module.BaseAddress)
            throw new InvalidOperationException("Workshop recipe dispatch table has an unsupported address base.");
        var table = imageBase + checked((nint)unchecked((uint)Marshal.ReadInt32(branch + 23)));
        var textStart = scanner.Module.BaseAddress + checked((nint)scanner.TextSectionOffset);
        if (table < textStart || table - textStart > scanner.TextSectionSize - 8)
            throw new InvalidOperationException("Workshop recipe dispatch table is outside executable storage.");
        RequireWithin(dispatch, projectRoute, 0x600);
        RequireRecipeRoutes(dispatch, project, category, projectRoute,
            imageBase + Marshal.ReadInt32(table), imageBase + Marshal.ReadInt32(table + 4));
        recipeReceiver = receiver;
        return receiver;
    }

    internal nint RequireMaterial()
    {
        if (materialReceiver != 0)
            return materialReceiver;
        var receiver = NativeCapability.ResolveUnique(scanner, MaterialReceiverSignature, "Workshop material receiver");
        var row = NativeCapability.ResolveUnique(scanner, MaterialRowSignature, "Workshop supply row argument");
        var quantity = NativeCapability.ResolveUnique(scanner, MaterialQuantitySignature, "Workshop native contribution quantity");
        var active = NativeCapability.ResolveUnique(scanner, MaterialRequestActiveSignature, "Workshop request state");
        RequireWithin(receiver, row, 0x500);
        RequireWithin(receiver, quantity, 0x500);
        RequireWithin(receiver, active, 0x500);
        if (row != active + 17 || quantity < row || quantity - row > 128)
            throw new InvalidOperationException("Workshop material command no longer checks request state before reading its row and quantity.");
        materialReceiver = receiver;
        return receiver;
    }

    internal static void RequireRecipeRoutes(nint dispatch, nint projectArgument, nint categoryArgument, nint projectRoute,
        nint commandOneTarget, nint commandTwoTarget)
    {
        RequireWithin(dispatch, commandOneTarget, 0x600);
        RequireWithin(dispatch, commandTwoTarget, 0x600);
        if (commandOneTarget != projectRoute || commandTwoTarget != categoryArgument || commandOneTarget > projectArgument ||
            projectArgument - commandOneTarget > 0x160)
            throw new InvalidOperationException("Workshop recipe commands no longer select the validated project/category argument paths.");
    }

    internal static void RequireWithin(nint entry, nint instruction, int length)
    {
        if (entry == 0 || instruction < entry || instruction - entry >= length)
            throw new InvalidOperationException("Workshop callback argument contract is outside its validated handler.");
    }
}
