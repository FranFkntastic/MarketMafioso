using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionSegmentedJsonlWriterTests
{
    [Fact]
    public void Write_RotatesIntoChecksummedContiguousSegments()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        using var writer = new MarketAcquisitionSegmentedJsonlWriter(directory, segmentSizeBytes: 32);
        writer.Write(1, """{"event":"one"}""");
        writer.Write(2, """{"event":"two","padding":"xxxxxxxx"}""");
        writer.Write(3, """{"event":"three"}""");
        writer.Dispose();

        Assert.Equal(3, writer.Segments.Count);
        Assert.Equal((1, 1), (writer.Segments[0].FirstSequence, writer.Segments[0].LastSequence));
        Assert.Equal((2, 2), (writer.Segments[1].FirstSequence, writer.Segments[1].LastSequence));
        Assert.Equal((3, 3), (writer.Segments[2].FirstSequence, writer.Segments[2].LastSequence));
        Assert.All(writer.Segments, segment =>
        {
            Assert.True(File.Exists(Path.Combine(directory, segment.FileName)));
            Assert.Equal(64, segment.Sha256.Length);
        });
    }
}
