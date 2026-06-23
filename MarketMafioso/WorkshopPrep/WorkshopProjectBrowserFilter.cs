using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopProjectBrowserFilter
{
    public static IReadOnlyList<WorkshopProjectDefinition> BuildVisibleProjects(
        IReadOnlyList<WorkshopProjectDefinition> projects,
        string search,
        IReadOnlyCollection<uint> favoriteWorkshopItemIds,
        bool favoritesOnly)
    {
        var favorites = favoriteWorkshopItemIds.Count == 0
            ? new HashSet<uint>()
            : new HashSet<uint>(favoriteWorkshopItemIds);

        return projects
            .Where(project => ProjectMatchesSearch(project, search))
            .Where(project => !favoritesOnly || favorites.Contains(project.WorkshopItemId))
            .OrderBy(project => favorites.Contains(project.WorkshopItemId) ? 0 : 1)
            .ToList();
    }

    public static bool ProjectMatchesSearch(WorkshopProjectDefinition project, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        var trimmed = search.Trim();
        return project.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               project.WorkshopItemId.ToString().Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               project.ResultItemId.ToString().Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               project.Materials.Any(material => material.ItemName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
