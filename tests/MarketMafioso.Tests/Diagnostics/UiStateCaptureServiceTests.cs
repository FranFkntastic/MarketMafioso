using MarketMafioso.Diagnostics;
using AtkEventType = Dalamud.Game.Addon.Events.AddonEventType;

namespace MarketMafioso.Tests.Diagnostics;

public sealed class UiStateCaptureServiceTests
{
    [Theory]
    [InlineData(AtkEventType.MouseMove)]
    [InlineData(AtkEventType.MouseOver)]
    [InlineData(AtkEventType.MouseOut)]
    [InlineData(AtkEventType.TimerTick)]
    [InlineData(AtkEventType.TimelineActiveLabelChanged)]
    [InlineData(AtkEventType.DragDropRollOver)]
    public void IsNoisyReceiveEvent_FiltersHighFrequencyNonSemanticEvents(AtkEventType eventType) =>
        Assert.True(UiStateCaptureService.IsNoisyReceiveEvent(eventType));

    [Theory]
    [InlineData(AtkEventType.ButtonClick)]
    [InlineData(AtkEventType.ListItemClick)]
    [InlineData(AtkEventType.ListItemDoubleClick)]
    [InlineData(AtkEventType.DragDropClick)]
    public void IsNoisyReceiveEvent_PreservesTransactionalEvents(AtkEventType eventType) =>
        Assert.False(UiStateCaptureService.IsNoisyReceiveEvent(eventType));
}
