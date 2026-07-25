using System.Numerics;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Tests.MarketDiagnostics;

public sealed class RemoteSummoningBellProbeTests
{
    [Fact]
    public void TryComputeOutwardDestination_ExtendsFromBellThroughPlayer()
    {
        var success = RemoteSummoningBellProbe.TryComputeOutwardDestination(
            new Vector3(2, 7, 0),
            Vector3.Zero,
            10,
            out var destination);

        Assert.True(success);
        Assert.Equal(new Vector3(12, 7, 0), destination);
    }

    [Fact]
    public void TryComputeOutwardDestination_RejectsCoincidentHorizontalPositions()
    {
        var success = RemoteSummoningBellProbe.TryComputeOutwardDestination(
            new Vector3(2, 7, 3),
            new Vector3(2, 1, 3),
            10,
            out _);

        Assert.False(success);
    }
}
