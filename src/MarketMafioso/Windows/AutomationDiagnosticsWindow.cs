using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.Windows;

public sealed class AutomationDiagnosticsWindow : Window
{
    private readonly IReadOnlyList<IAutomationDiagnosticProbe> probes;
    private AutomationDiagnosticProbeResult? lastResult;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public AutomationDiagnosticsWindow(IReadOnlyList<IAutomationDiagnosticProbe> probes)
        : base("Automation Diagnostics##AutomationDiagnostics")
    {
        this.probes = probes;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.TextColored(ColHeader, "Automation Diagnostics");
        ImGui.Separator();

        foreach (var probe in probes)
        {
            if (ImGui.Button($"{probe.Name}##automationProbe{probe.Name}"))
                RunProbe(probe);
        }

        ImGui.Spacing();
        if (lastResult == null)
        {
            ImGui.TextColored(ColMuted, "No probe has run this session.");
            return;
        }

        ImGui.TextColored(lastResult.IsSuccess ? ColSuccess : ColError, lastResult.ProbeName);
        ImGui.TextWrapped(lastResult.Message);

        if (lastResult.Details.Count == 0)
            return;

        ImGui.Spacing();
        if (ImGui.BeginTable("AutomationDiagnosticProbeResultDetails", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Field");
            ImGui.TableSetupColumn("Value");
            ImGui.TableHeadersRow();

            foreach (var (key, value) in lastResult.Details.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(key);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
            }

            ImGui.EndTable();
        }
    }

    private void RunProbe(IAutomationDiagnosticProbe probe)
    {
        try
        {
            lastResult = probe.Run();
        }
        catch (Exception ex)
        {
            lastResult = new AutomationDiagnosticProbeResult(
                probe.Name,
                IsSuccess: false,
                $"Probe failed: {ex.Message}",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["exceptionMessage"] = ex.Message,
                });
        }
    }
}
