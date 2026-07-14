using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter;

public sealed class OutfitterTargetCatalog
{
    public IReadOnlyList<OutfitterTarget> Build(
        CharacterEquipmentSnapshot snapshot,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(retainers);

        var targets = new List<OutfitterTarget>();
        foreach (var job in snapshot.Jobs
                     .Where(job => job.IsUnlocked == true && job.Level > 0)
                     .OrderBy(job => DisciplineOrder(job.Discipline))
                     .ThenBy(job => job.Role, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase))
        {
            var gearsets = snapshot.Gearsets
                .Where(gearset => gearset.IsValid && gearset.ClassJobId == job.ClassJobId)
                .OrderBy(gearset => gearset.GearsetId)
                .ToArray();
            targets.Add(new(
                $"job:{job.ClassJobId}",
                OutfitterTargetKind.Job,
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(job.Name),
                $"{job.Abbreviation} · Lv. {job.Level:N0} · {gearsets.Length:N0} gearset{(gearsets.Length == 1 ? string.Empty : "s")}",
                Job: job,
                Gearset: gearsets.FirstOrDefault()));
            foreach (var gearset in gearsets)
            {
                targets.Add(new(
                    $"gearset:{gearset.GearsetId}",
                    OutfitterTargetKind.Gearset,
                    gearset.Name,
                    $"Gearset {gearset.GearsetId + 1:N0}",
                    Job: job,
                    Gearset: gearset));
            }
        }

        foreach (var retainer in retainers.Values
                     .OrderBy(retainer => retainer.RetainerName, StringComparer.OrdinalIgnoreCase))
        {
            var age = DateTime.UtcNow - retainer.LastUpdated.ToUniversalTime();
            var freshness = age.TotalHours < 1
                ? $"updated {Math.Max(1, (int)age.TotalMinutes):N0}m ago"
                : age.TotalDays < 2
                    ? $"updated {Math.Max(1, (int)age.TotalHours):N0}h ago"
                    : $"updated {Math.Max(1, (int)age.TotalDays):N0}d ago";
            targets.Add(new(
                $"retainer:{retainer.RetainerId}",
                OutfitterTargetKind.Retainer,
                retainer.RetainerName,
                $"Retainer · {freshness}",
                Retainer: retainer,
                IsReady: false,
                Diagnostic: "Retainer inventory is cached, but AutoRetainer does not expose the worn equipment slots needed to solve this loadout yet."));
        }

        return targets;
    }

    private static int DisciplineOrder(EquipmentDiscipline discipline) => discipline switch
    {
        EquipmentDiscipline.Combat => 0,
        EquipmentDiscipline.Crafter => 1,
        EquipmentDiscipline.Gatherer => 2,
        _ => 3,
    };
}
