using System.Numerics;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Tests.MarketDiagnostics;

public sealed class PositionFrameOneShotPreparationSlotTests
{
    [Fact]
    public void Consume_ReturnsPreparationExactlyOnce()
    {
        var slot = new PositionFrameOneShotPreparationSlot();
        var preparation = CreatePreparation();
        slot.Store(preparation);

        Assert.Same(preparation, slot.Consume());
        Assert.Null(slot.Consume());
        Assert.Null(slot.Peek());
    }

    [Fact]
    public void Cancel_ClearsPreparationWithoutConsumption()
    {
        var slot = new PositionFrameOneShotPreparationSlot();
        slot.Store(CreatePreparation());

        Assert.True(slot.Cancel());
        Assert.Null(slot.Peek());
        Assert.Null(slot.Consume());
        Assert.False(slot.Cancel());
    }

    private static PositionFrameOneShotPreparation CreatePreparation()
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid(),
            now,
            now.AddSeconds(60),
            339,
            0x1234,
            5,
            4.75f,
            new Vector3(1, 2, 3),
            new Vector3(4, 5, 6),
            new Vector3(3.5f, 5, 6));
    }
}
