using System.Numerics;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopFabricationStationSelectionTests
{
    private sealed record Station(string Name, Vector3 Position);

    [Fact]
    public void NearestStationWinsRegardlessOfObjectTableOrder()
    {
        // Mirrors the Primary workshop: the table lists the upper-platform station first.
        var upper = new Station("upper", new Vector3(-6, 3, -5));
        var beside = new Station("beside", new Vector3(1, 0, 1));
        Assert.Same(beside, Select([upper, beside], Vector3.Zero));
        Assert.Same(beside, Select([beside, upper], Vector3.Zero));
    }

    [Fact]
    public void HeightCountsSoAStationOnAnotherFloorLoses()
    {
        var aboveButFlatClose = new Station("above", new Vector3(1, 6, 0));
        var level = new Station("level", new Vector3(3, 0, 3));
        Assert.Same(level, Select([aboveButFlatClose, level], Vector3.Zero));
    }

    [Fact]
    public void StationsOutOfReachAreNotSelected() =>
        Assert.Null(Select([new Station("far", new Vector3(20, 0, 0))], Vector3.Zero));

    private static Station? Select(Station[] stations, Vector3 player) =>
        WorkshopFabricationStationSelection.SelectNearest(stations, player, x => x.Position, 8f);
}
