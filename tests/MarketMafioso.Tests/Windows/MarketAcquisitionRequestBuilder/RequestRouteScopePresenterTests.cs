using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class RequestRouteScopePresenterTests
{
    [Fact]
    public void ApplyRegion_ClearsSelectedDataCenters()
    {
        var scope = Scope() with
        {
            SweepDataCenters = ["Aether", "Primal"],
        };

        var updated = RequestRouteScopePresenter.ApplyRegion(scope, "Europe");

        Assert.Equal("Europe", updated.Region);
        Assert.Empty(updated.SweepDataCenters);
    }

    [Fact]
    public void ApplyWorldMode_WhenLeavingAllWorldSweep_ResetsSweepScopeAndDataCenters()
    {
        var scope = Scope() with
        {
            WorldMode = "AllWorldSweep",
            SweepScope = "DataCenters",
            SweepDataCenters = ["Aether"],
        };

        var updated = RequestRouteScopePresenter.ApplyWorldMode(scope, "Recommended");

        Assert.Equal("Recommended", updated.WorldMode);
        Assert.Equal("Region", updated.SweepScope);
        Assert.Empty(updated.SweepDataCenters);
    }

    [Fact]
    public void ApplySweepScope_WhenLeavingDataCenters_ClearsSelectedDataCenters()
    {
        var scope = Scope() with
        {
            SweepScope = "DataCenters",
            SweepDataCenters = ["Aether"],
        };

        var updated = RequestRouteScopePresenter.ApplySweepScope(scope, "CurrentDataCenter");

        Assert.Equal("CurrentDataCenter", updated.SweepScope);
        Assert.Empty(updated.SweepDataCenters);
    }

    [Fact]
    public void ToggleDataCenter_ReplacesExistingSelectionCaseInsensitively()
    {
        var scope = Scope() with
        {
            SweepDataCenters = ["aether", "Primal"],
        };

        var updated = RequestRouteScopePresenter.ToggleDataCenter(scope, "Aether", selected: true);

        Assert.Equal(["Primal", "Aether"], updated.SweepDataCenters);
    }

    [Fact]
    public void ToggleDataCenter_WhenUnselected_RemovesSelectionCaseInsensitively()
    {
        var scope = Scope() with
        {
            SweepDataCenters = ["aether", "Primal"],
        };

        var updated = RequestRouteScopePresenter.ToggleDataCenter(scope, "Aether", selected: false);

        Assert.Equal(["Primal"], updated.SweepDataCenters);
    }

    private static RequestRouteScope Scope() => RequestRouteScope.Default with
    {
        WorldMode = "AllWorldSweep",
        SweepScope = "DataCenters",
    };
}
