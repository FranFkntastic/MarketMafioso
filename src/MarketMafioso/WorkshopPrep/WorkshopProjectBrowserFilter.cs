using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopProjectBrowserFilter
{
    public static IReadOnlyList<WorkshopProjectDefinition> BuildVisibleProjects(
        IReadOnlyList<WorkshopProjectDefinition> projects,
        string search)
    {
        return projects
            .Where(project => ProjectMatchesSearch(project, search))
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
