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
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
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

        if (ImGui.Checkbox("Favorites only##workshopProjectBrowserFavoritesOnly", ref selection.FavoritesOnly))
            ClearCheckedProjectsNotInView(projects);

        ImGui.SameLine();
        ImGui.TextColored(ColMuted, $"{selection.CheckedWorkshopItemIds.Count} checked");

        var filteredProjects = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            selection.Search,
            config.FavoriteWorkshopProjectIds,
            selection.FavoritesOnly);

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

        var checkWidth = Math.Max(ImGui.CalcTextSize("Sel").X, ImGui.GetFrameHeight()) + 16;
        var favoriteWidth = Math.Max(ImGui.CalcTextSize("Fav").X, ImGui.GetFrameHeight()) + 16;
        var projectWidth = CalculateProjectColumnWidth(projects);
        var materialsWidth = Math.Max(ImGui.CalcTextSize("Materials").X, ImGui.CalcTextSize("999").X) + 24;
        var queuedWidth = Math.Max(ImGui.CalcTextSize("Queued").X, ImGui.CalcTextSize("9999").X) + 24;
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("WorkshopProjectBrowserTable", 5, flags, new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("Sel", ImGuiTableColumnFlags.WidthFixed, checkWidth);
            ImGui.TableSetupColumn("Fav", ImGuiTableColumnFlags.WidthFixed, favoriteWidth);
            ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthFixed, projectWidth);
            ImGui.TableSetupColumn("Materials", ImGuiTableColumnFlags.WidthFixed, materialsWidth);
            ImGui.TableSetupColumn("Queued", ImGuiTableColumnFlags.WidthFixed, queuedWidth);
            ImGui.TableHeadersRow();

            foreach (var project in projects)
            {
                var isSelected = project.WorkshopItemId == selection.SelectedWorkshopItemId;
                var isChecked = selection.CheckedWorkshopItemIds.Contains(project.WorkshopItemId);
                var isFavorite = config.FavoriteWorkshopProjectIds.Contains(project.WorkshopItemId);
                var queuedQuantity = GetQueuedQuantity(project.WorkshopItemId);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Checkbox($"##workshopProjectBrowserCheck{project.WorkshopItemId}", ref isChecked))
                    SetProjectChecked(project.WorkshopItemId, isChecked);

                ImGui.TableNextColumn();
                if (ImGui.Checkbox($"##workshopProjectBrowserFavorite{project.WorkshopItemId}", ref isFavorite))
                    SetFavorite(project.WorkshopItemId, isFavorite);

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
                if (queuedQuantity > 0)
                    ImGui.TextColored(ColSuccess, queuedQuantity.ToString());
                else
                    ImGui.TextColored(ColMuted, "0");
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

        if (ImGui.BeginTable("WorkshopProjectBrowserMaterials", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Material");
            ImGui.TableSetupColumn("Qty");
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
        var hasCheckedProjects = selection.CheckedWorkshopItemIds.Count > 0;
        if (!hasCheckedProjects)
            ImGui.BeginDisabled();

        if (ImGui.Button("Add Checked"))
            AddCheckedProjects();

        ImGui.SameLine();
        if (ImGui.Button("Add Checked & Close"))
        {
            AddCheckedProjects();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Checked"))
            selection.CheckedWorkshopItemIds.Clear();

        if (!hasCheckedProjects)
            ImGui.EndDisabled();

        if (project == null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Close"))
                IsOpen = false;
            return;
        }

        if (ImGui.Button("Add Selected"))
            addProject(project.WorkshopItemId);

        ImGui.SameLine();
        if (ImGui.Button("Add Selected & Close"))
        {
            addProject(project.WorkshopItemId);
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

    private void SetProjectChecked(uint workshopItemId, bool isChecked)
    {
        if (isChecked)
            selection.CheckedWorkshopItemIds.Add(workshopItemId);
        else
            selection.CheckedWorkshopItemIds.Remove(workshopItemId);
    }

    private void SetFavorite(uint workshopItemId, bool isFavorite)
    {
        if (isFavorite)
        {
            if (!config.FavoriteWorkshopProjectIds.Contains(workshopItemId))
                config.FavoriteWorkshopProjectIds.Add(workshopItemId);
        }
        else
        {
            config.FavoriteWorkshopProjectIds.RemoveAll(x => x == workshopItemId);
            if (selection.FavoritesOnly)
                selection.CheckedWorkshopItemIds.Remove(workshopItemId);
        }

        config.Save();
    }

    private void AddCheckedProjects()
    {
        foreach (var workshopItemId in selection.CheckedWorkshopItemIds.ToList())
            addProject(workshopItemId);
    }

    private void ClearCheckedProjectsNotInView(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (!selection.FavoritesOnly)
            return;

        var favorites = new HashSet<uint>(config.FavoriteWorkshopProjectIds);
        selection.CheckedWorkshopItemIds.RemoveWhere(workshopItemId =>
            !favorites.Contains(workshopItemId) ||
            projects.All(project => project.WorkshopItemId != workshopItemId));
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
    public bool FavoritesOnly;
    public HashSet<uint> CheckedWorkshopItemIds { get; } = new();
}
