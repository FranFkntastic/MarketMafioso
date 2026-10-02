using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopCallbackProtocolTests
{
    [Theory]
    [InlineData("Project")] [InlineData("Category")] [InlineData("Contribution")] [InlineData("Close")]
    public void EachOperationAcceptsItsTypedArgumentsAndRejectsChangedOpcodeCountAndTypes(string name)
    {
        var operation = Enum.Parse<WorkshopCallbackOperation>(name);
        var values = Payload(operation);
        WorkshopCallbackProtocol.RequirePayload(operation, values);
        var wrongCommand = (AtkValue[])values.Clone(); wrongCommand[0].Int = 99;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequirePayload(operation, wrongCommand));
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequirePayload(operation, values[..^1]));
        var wrongType = (AtkValue[])values.Clone(); wrongType[0].Type = AtkValueType.UInt;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequirePayload(operation, wrongType));
    }

    [Fact]
    public void AProjectCannotBeDispatchedWithTheCategoryPayload() =>
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequirePayload(WorkshopCallbackOperation.Project, Payload(WorkshopCallbackOperation.Category)));

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x9000)]
    public void RelocatedMatchingOwnerAndCallbackRoutesRemainAvailable(int origin)
    {
        WorkshopCallbackProtocol.RequireOwner(origin, origin, 7, 7, origin + 0x100, origin + 0x100);
        WorkshopNativeCallbackCapability.RequireRecipeRoutes(origin, origin + 0xef, origin + 0x251, origin + 0x65, origin + 0x65, origin + 0x251);
    }

    [Theory]
    [InlineData(0, 0x1000, 7, 7, 0x2000, 0x2000)]
    [InlineData(0x1000, 0x1001, 7, 7, 0x2000, 0x2000)]
    [InlineData(0x1000, 0x1000, 0, 0, 0x2000, 0x2000)]
    [InlineData(0x1000, 0x1000, 7, 8, 0x2000, 0x2000)]
    [InlineData(0x1000, 0x1000, 7, 7, 0, 0x2000)]
    [InlineData(0x1000, 0x1000, 7, 7, 0x2001, 0x2000)]
    public void StaleAddonWrongOwnerAndChangedNativeReceiverAreRejected(int captured, int current, uint addonId, uint ownerId, int receiver, int expected) =>
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireOwner(captured, current, addonId, ownerId, receiver, expected));

    [Fact]
    public void SwappedRecipeCommandRoutesAreRejected() =>
        Assert.Throws<InvalidOperationException>(() => WorkshopNativeCallbackCapability.RequireRecipeRoutes(0x1000, 0x10ef, 0x1251, 0x1065, 0x1251, 0x1065));

    [Fact]
    public void ArgumentContractFromAnUnrelatedFunctionIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => WorkshopNativeCallbackCapability.RequireWithin(0x1000, 0x1800, 0x600));

    [Fact]
    public void ChangedCategoryHandlerIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => WorkshopNativeCallbackCapability.RequireRecipeRoutes(0x1000, 0x10ef, 0x1251, 0x1065, 0x1065, 0x1252));

    [Fact]
    public void ProjectMustStillBeUniquelyVisible()
    {
        WorkshopCallbackProtocol.RequireProjectVisible([new(12, "Localized project")], 12);
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireProjectVisible([new(13, "Different project")], 12));
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireProjectVisible([new(12, "One"), new(12, "Two")], 12));
    }

    [Fact]
    public void ValidTypedRecipeAndMaterialLayoutsAreAccepted()
    {
        WorkshopCallbackProtocol.RequireCraftingLogSchema(RecipeValues());
        WorkshopCallbackProtocol.RequireMaterialSchema(MaterialValues(), 12);
        WorkshopCallbackProtocol.RequireContribution(Craft(), Craft(), 0, Material(), 1, false, 100, [20], [5]);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(17)]
    public void RecipeLayoutTypeChangesAreRejected(int index)
    {
        var values = RecipeValues(); values[index].Type = AtkValueType.Int;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireCraftingLogSchema(values));
    }

    [Fact]
    public void RecipeRowCountCannotReadPastTheValueBuffer()
    {
        var values = RecipeValues(); values[13].UInt = uint.MaxValue;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireCraftingLogSchema(values));
    }

    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(7)] [InlineData(11)] [InlineData(12)]
    [InlineData(36)] [InlineData(60)] [InlineData(108)] [InlineData(120)] [InlineData(132)]
    public void MaterialLayoutTypeChangesAreRejected(int index)
    {
        var values = MaterialValues(); values[index].Type = AtkValueType.Int;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireMaterialSchema(values, 12));
    }

    [Fact]
    public void MaterialRowCountCannotExceedTheNativeSupplyArray()
    {
        var values = MaterialValues(); values[11].UInt = 13;
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireMaterialSchema(values, 12));
    }

    [Theory]
    [InlineData("project")] [InlineData("phase")] [InlineData("item")] [InlineData("quantity")]
    [InlineData("index")] [InlineData("mode")] [InlineData("busy")] [InlineData("native-item")]
    [InlineData("native-quantity")] [InlineData("finished")]
    public void ChangedContributionStateIsRejectedBeforeDispatch(string change)
    {
        var current = Craft(); var requested = Material(); var index = 0;
        byte mode = 1; var busy = false; uint nativeItem = 20; byte nativeQuantity = 5;
        switch (change)
        {
            case "project": current = current with { ResultItem = 101 }; break;
            case "phase": current = current with { StepsComplete = 1 }; break;
            case "item": current = current with { Items = [requested with { ItemId = 21 }] }; break;
            case "quantity": current = current with { Items = [requested with { ItemCountPerStep = 6 }] }; break;
            case "index": index = -1; break;
            case "mode": mode = 2; break;
            case "busy": busy = true; break;
            case "native-item": nativeItem = 21; break;
            case "native-quantity": nativeQuantity = 6; break;
            case "finished": requested = requested with { Finished = true }; break;
        }
        Assert.Throws<InvalidOperationException>(() => WorkshopCallbackProtocol.RequireContribution(Craft(), current,
            index, requested, mode, busy, 100, [nativeItem], [nativeQuantity]));
    }

    private static WorkshopCraftMaterialState Material() => new(20, "Localized material", 5, 0, 3, false);
    private static WorkshopCraftState Craft() => new(100, 0, 2, [Material()]);
    private static unsafe AtkValue Text() => new() { Type = AtkValueType.String, String = (byte*)1 };
    private static AtkValue UInt(uint value) => new() { Type = AtkValueType.UInt, UInt = value };
    private static AtkValue[] Payload(WorkshopCallbackOperation operation)
    {
        var values = new AtkValue[operation is WorkshopCallbackOperation.Project or WorkshopCallbackOperation.Category ? 8 :
            operation == WorkshopCallbackOperation.Contribution ? 4 : 1];
        values[0] = new() { Type = AtkValueType.Int, Int = operation switch {
            WorkshopCallbackOperation.Project => 1, WorkshopCallbackOperation.Category => 2,
            WorkshopCallbackOperation.Contribution => 0, _ => -1 } };
        if (operation == WorkshopCallbackOperation.Project) values[4] = UInt(12);
        if (operation == WorkshopCallbackOperation.Category)
        {
            values[2] = UInt(2); values[3] = UInt(3);
            values[4] = UInt(0); values[5] = UInt(0); values[6] = UInt(0);
        }
        if (operation == WorkshopCallbackOperation.Contribution) { values[1] = UInt(0); values[2] = UInt(5); }
        return values;
    }
    private static AtkValue[] RecipeValues()
    {
        var values = new AtkValue[18]; values[13] = UInt(1); values[14] = UInt(12); values[17] = Text(); return values;
    }
    private static AtkValue[] MaterialValues()
    {
        var values = new AtkValue[157];
        values[0] = UInt(100); values[6] = UInt(0); values[7] = UInt(2); values[11] = UInt(1);
        values[12] = UInt(20); values[36] = Text(); values[60] = UInt(5); values[108] = UInt(0);
        values[120] = UInt(3); values[132] = UInt(0); return values;
    }
}
