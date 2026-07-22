using System.Buffers.Binary;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class DalamudMarketPurchasePacketObserverTests
{
    [Theory]
    [InlineData(42u, false)]
    [InlineData(1_000_042u, true)]
    public void Decode_reads_catalog_quality_and_quantity_from_server_purchase_packet(uint rawCatalogId, bool highQuality)
    {
        Span<byte> packet = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, rawCatalogId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], 7);

        var decoded = DalamudMarketPurchasePacketObserver.TryDecode(packet, out var result);

        Assert.True(decoded);
        Assert.Equal(rawCatalogId, result.RawCatalogId);
        Assert.Equal(42u, result.ItemId);
        Assert.Equal(highQuality, result.IsHighQuality);
        Assert.Equal(7u, result.Quantity);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(42, 0)]
    public void Decode_rejects_zero_identity_or_quantity(uint rawCatalogId, uint quantity)
    {
        Span<byte> packet = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, rawCatalogId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], quantity);

        Assert.False(DalamudMarketPurchasePacketObserver.TryDecode(packet, out _));
    }
}
