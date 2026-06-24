using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public sealed class WorkshopProjectBrowserWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly WorkshopProjectSelectionState selection;
    private readonly Action<uint> addProject;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public WorkshopProjectBrowserWindow(
        Configuration config,
        WorkshopProjectCatalog workshopCatalog,
        WorkshopProjectSelectionState selection,
        Action<uint> addProject)
        : base("Workshop Project Browser##MarketMafiosoWorkshopProjectBrowser", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.workshopCatalog = workshopCatalog;
        this.selection = selection;
        this.addProject = addProject;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var projects = workshopCatalog.GetProjects();
        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No company workshop projects were found.");
            return;
        }

        ImGui.Text("Search:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##workshopProjectBrowserSearch", ref selection.Search, 256);

        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Quantity##workshopProjectBrowserQuantity", ref selection.Quantity))
            selection.Quantity = Math.Max(1, selection.Quantity);

        var filteredProjects = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            selection.Search);

        ImGui.Spacing();
        DrawProjectTable(filteredProjects);
        ImGui.Spacing();
        DrawSelectedProject(projects);
    }

    private void DrawProjectTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No matching workshop projects.");
            return;
        }

        var projectWidth = CalculateProjectColumnWidth(projects);
        var materialsWidth = Math.Max(ImGui.CalcTextSize("Materials").X, ImGui.CalcTextSize("999").X) + 24;
        var queuedWidth = Math.Max(ImGui.CalcTextSize("Queued").X, ImGui.CalcTextSize("9999").X) + 24;
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("WorkshopProjectBrowserTable", 3, flags, new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthFixed, projectWidth);
            ImGui.TableSetupColumn("Materials", ImGuiTableColumnFlags.WidthFixed, materialsWidth);
            ImGui.TableSetupColumn("Queued", ImGuiTableColumnFlags.WidthFixed, queuedWidth);
            ImGui.TableHeadersRow();

            foreach (var project in projects)
            {
                var isSelected = project.WorkshopItemId == selection.SelectedWorkshopItemId;
                var queuedQuantity = GetQueuedQuantity(project.WorkshopItemId);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{project.Name}##workshopProjectBrowser{project.WorkshopItemId}", isSelected))
                    selection.SelectedWorkshopItemId = project.WorkshopItemId;

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    selection.SelectedWorkshopItemId = project.WorkshopItemId;
                    addProject(project.WorkshopItemId);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(project.Materials.Count.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(queuedQuantity > 0 ? ColHeader : ColMuted, queuedQuantity.ToString());
            }

            ImGui.EndTable();
        }
    }

    private void DrawSelectedProject(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var project = projects.FirstOrDefault(x => x.WorkshopItemId == selection.SelectedWorkshopItemId);
        if (project == null)
        {
            ImGui.TextColored(ColMuted, "Select a project to preview materials.");
            DrawProjectActions(null);
            return;
        }

        ImGui.TextColored(ColHeader, project.Name);

        if (ImGui.BeginTable("WorkshopProjectBrowserMaterials", 2, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableHeadersRow();

            foreach (var material in project.Materials.Take(8))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(material.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(material.Quantity.ToString());
            }

            ImGui.EndTable();
        }

        if (project.Materials.Count > 8)
            ImGui.TextColored(ColMuted, $"{project.Materials.Count - 8} more material(s).");

        DrawProjectActions(project);
    }

    private void DrawProjectActions(WorkshopProjectDefinition? project)
    {
        ImGui.Checkbox("Close after add##workshopProjectBrowserCloseAfterAdd", ref selection.CloseAfterAdd);

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Selected", project != null))
        {
            if (project == null)
                throw new InvalidOperationException("Selected workshop project is unavailable.");

            addProject(project.WorkshopItemId);
            if (selection.CloseAfterAdd)
                IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;
    }

    private float CalculateProjectColumnWidth(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var width = ImGui.CalcTextSize("Project").X;
        foreach (var project in projects)
            width = Math.Max(width, ImGui.CalcTextSize(project.Name).X);

        return Math.Clamp(width + 28, 220, Math.Max(260, ImGui.GetContentRegionAvail().X - 180));
    }

    private int GetQueuedQuantity(uint workshopItemId)
    {
        return config.WorkshopPrepQueue
            .Where(item => item.WorkshopItemId == workshopItemId)
            .Sum(item => item.Quantity);
    }

    public void Dispose()
    {
    }
}

public sealed class WorkshopProjectSelectionState
{
    public uint SelectedWorkshopItemId;
    public string Search = string.Empty;
    public int Quantity = 1;
    public bool CloseAfterAdd;
}
