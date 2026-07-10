using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopAssemblyPanel
{
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly Func<string> getWorkshopStatus;
    private readonly Action<string> setWorkshopStatus;
    private readonly Action<bool> startWorkshopAssembly;

    public WorkshopAssemblyPanel(
        WorkshopAssemblyRunner workshopAssemblyRunner,
        WorkshopRetainerRestockService workshopRetainerRestock,
        Func<string> getWorkshopStatus,
        Action<string> setWorkshopStatus,
        Action<bool> startWorkshopAssembly)
    {
        this.workshopAssemblyRunner = workshopAssemblyRunner ?? throw new ArgumentNullException(nameof(workshopAssemblyRunner));
        this.workshopRetainerRestock = workshopRetainerRestock ?? throw new ArgumentNullException(nameof(workshopRetainerRestock));
        this.getWorkshopStatus = getWorkshopStatus ?? throw new ArgumentNullException(nameof(getWorkshopStatus));
        this.setWorkshopStatus = setWorkshopStatus ?? throw new ArgumentNullException(nameof(setWorkshopStatus));
        this.startWorkshopAssembly = startWorkshopAssembly ?? throw new ArgumentNullException(nameof(startWorkshopAssembly));
    }

    public void Draw(bool hasPrepQueue)
    {
        var actionWidth = workshopAssemblyRunner.HasActiveRun ? 280f : 140f;
        ImGuiUi.SectionHeaderWithActions(
            "Assembly Workflow",
            MarketMafiosoUiTheme.Header,
            () => DrawActions(hasPrepQueue),
            actionWidth);

        var workshopStatus = getWorkshopStatus();
        ImGui.TextColored(GetWorkshopStatusColor(workshopStatus), workshopStatus);
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted, workshopRetainerRestock.LastStatus);

        var progress = workshopAssemblyRunner.Progress;
        ImGui.TextColored(workshopAssemblyRunner.HasActiveRun ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted, progress.Message);
        if (progress.TotalProjects > 0)
        {
            var completed = Math.Clamp(progress.CompletedProjects, 0, progress.TotalProjects);
            var fraction = completed / (float)progress.TotalProjects;
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Assembly progress: {completed}/{progress.TotalProjects}");
            ImGui.SameLine();
            ImGui.ProgressBar(fraction, new Vector2(210, 0), string.Empty);
        }
    }

    private void DrawActions(bool hasPrepQueue)
    {
        if (workshopAssemblyRunner.IsPaused)
        {
            if (ImGui.Button("Resume"))
                setWorkshopStatus(workshopAssemblyRunner.Resume().Message);

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                setWorkshopStatus("Workshop assembly stopped.");
            }
        }
        else if (workshopAssemblyRunner.IsRunning)
        {
            if (ImGui.Button("Pause"))
                setWorkshopStatus(workshopAssemblyRunner.Pause().Message);

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                setWorkshopStatus("Workshop assembly stopped.");
            }
        }

        if (workshopAssemblyRunner.HasActiveRun)
            ImGui.SameLine();

        if (ImGuiUi.MenuButton("Start Options", !workshopAssemblyRunner.HasActiveRun && hasPrepQueue))
            ImGui.OpenPopup("WorkshopAssemblyStartMenu");

        if (ImGui.BeginPopup("WorkshopAssemblyStartMenu"))
        {
            if (ImGuiUi.MenuItem("Start Assembly", hasPrepQueue))
                startWorkshopAssembly(false);

            if (ImGuiUi.MenuItem("Start With Diagnostics", hasPrepQueue))
                startWorkshopAssembly(true);

            ImGui.EndPopup();
        }
    }

    private static Vector4 GetWorkshopStatusColor(string workshopStatus)
    {
        if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;

        if (workshopStatus.Contains("copied", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("added", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Success;

        return MarketMafiosoUiTheme.Muted;
    }
}
