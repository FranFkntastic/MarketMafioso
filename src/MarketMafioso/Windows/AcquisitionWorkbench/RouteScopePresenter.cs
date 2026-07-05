using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public sealed record AcquisitionRouteScope(
    string Region,
    string WorldMode,
    string SweepScope,
    IReadOnlyList<string> SweepDataCenters)
{
    public static AcquisitionRouteScope Default { get; } = new(
        "North America",
        "Recommended",
        "Region",
        []);

    public static AcquisitionRouteScope FromDraft(MarketAcquisitionQuickShopDraft draft) =>
        new(
            draft.Region,
            draft.WorldMode,
            draft.SweepScope,
            draft.SweepDataCenters.ToArray());
}

public static class RouteScopePresenter
{
    public static readonly IReadOnlyList<string> WorldModes = ["Recommended", "AllWorldSweep"];
    public static readonly IReadOnlyList<string> SweepScopes = ["Region", "CurrentDataCenter", "DataCenters"];

    public static AcquisitionRouteScope ApplyRegion(AcquisitionRouteScope scope, string region) =>
        scope with
        {
            Region = region,
            SweepDataCenters = [],
        };

    public static AcquisitionRouteScope ApplyWorldMode(AcquisitionRouteScope scope, string worldMode) =>
        scope with
        {
            WorldMode = worldMode,
            SweepScope = worldMode == "AllWorldSweep" ? scope.SweepScope : "Region",
            SweepDataCenters = worldMode == "AllWorldSweep" ? scope.SweepDataCenters : [],
        };

    public static AcquisitionRouteScope ApplySweepScope(AcquisitionRouteScope scope, string sweepScope) =>
        scope with
        {
            SweepScope = sweepScope,
            SweepDataCenters = sweepScope == "DataCenters" ? scope.SweepDataCenters : [],
        };

    public static AcquisitionRouteScope ToggleDataCenter(AcquisitionRouteScope scope, string dataCenter, bool selected)
    {
        var selectedDataCenters = scope.SweepDataCenters
            .Where(existing => !existing.Equals(dataCenter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selected)
            selectedDataCenters.Add(dataCenter);

        return scope with { SweepDataCenters = selectedDataCenters };
    }
}
