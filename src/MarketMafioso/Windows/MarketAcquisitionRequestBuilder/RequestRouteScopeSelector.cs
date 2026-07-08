using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestRouteScopeSelector
{
    public static void Draw(
        string id,
        RequestRouteScope scope,
        Action<RequestRouteScope> onChanged,
        Vector4 mutedColor,
        Vector4 errorColor)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(onChanged);

        DrawFullWidthCombo(
            $"Region##{id}Region",
            MarketAcquisitionWorldCatalog.SupportedRegions.ToArray(),
            scope.Region,
            region => onChanged(RequestRouteScopePresenter.ApplyRegion(scope, region)),
            mutedColor);

        DrawFullWidthCombo(
            $"World Mode##{id}WorldMode",
            RequestRouteScopePresenter.WorldModes,
            scope.WorldMode,
            worldMode => onChanged(RequestRouteScopePresenter.ApplyWorldMode(scope, worldMode)),
            mutedColor);

        if (scope.WorldMode != "AllWorldSweep")
            return;

        DrawFullWidthCombo(
            $"Sweep Scope##{id}SweepScope",
            RequestRouteScopePresenter.SweepScopes,
            scope.SweepScope,
            sweepScope => onChanged(RequestRouteScopePresenter.ApplySweepScope(scope, sweepScope)),
            mutedColor);

        if (scope.SweepScope == "DataCenters")
            DrawDataCenterSelector(id, scope, onChanged, errorColor);
    }

    private static void DrawDataCenterSelector(
        string id,
        RequestRouteScope scope,
        Action<RequestRouteScope> onChanged,
        Vector4 errorColor)
    {
        IReadOnlyDictionary<string, string[]> dataCenters;
        try
        {
            dataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(scope.Region);
        }
        catch (InvalidOperationException ex)
        {
            ImGui.TextColored(errorColor, ex.Message);
            return;
        }

        foreach (var dataCenter in dataCenters.Keys)
        {
            var selected = scope.SweepDataCenters.Contains(dataCenter, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"{dataCenter}##{id}Dc{dataCenter}", ref selected))
                onChanged(RequestRouteScopePresenter.ToggleDataCenter(scope, dataCenter, selected));

            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private static void DrawFullWidthCombo(
        string label,
        IReadOnlyList<string> options,
        string current,
        Action<string> onChanged,
        Vector4 mutedColor)
    {
        ImGui.TextColored(mutedColor, label.Split('#')[0]);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, string.IsNullOrWhiteSpace(current) ? "-" : current))
            return;

        foreach (var option in options)
        {
            var isSelected = option.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(option, isSelected) && !isSelected)
                onChanged(option);
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }
}
