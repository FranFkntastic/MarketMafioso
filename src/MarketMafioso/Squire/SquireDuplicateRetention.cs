using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public static class SquireDuplicateRetention
{
    public static IReadOnlyList<SquireDuplicateRetentionRule> Merge(
        IReadOnlyList<SquireDuplicateRetentionRule>? approved,
        IReadOnlyList<SquireDuplicateRetentionRule>? current) =>
        (approved ?? []).Concat(current ?? [])
            .Where(rule => rule.ItemId != 0 && rule.MinimumCopies > 0)
            .GroupBy(rule => new { rule.ItemId, rule.IsHighQuality })
            .Select(group => new SquireDuplicateRetentionRule(
                group.Key.ItemId,
                group.Key.IsHighQuality,
                group.Max(rule => rule.MinimumCopies)))
            .ToArray();

    public static bool DoesNotReduceRequiredMultiplicity(
        IEnumerable<EquipmentInstanceSnapshot> before,
        IEnumerable<EquipmentInstanceSnapshot> after,
        SquireProtectionPolicy policy,
        out string message)
    {
        var beforeArray = before.ToArray();
        var afterArray = after.ToArray();
        foreach (var rule in Merge(policy.DuplicateRetentionRules, null))
        {
            var beforeCount = Count(beforeArray, rule);
            var afterCount = Count(afterArray, rule);
            var required = Math.Min(beforeCount, rule.MinimumCopies);
            if (afterCount >= required)
                continue;
            var quality = rule.IsHighQuality ? "HQ" : "normal-quality";
            message = $"The selected batch would leave {afterCount} {quality} copies of item {rule.ItemId}; this character's duplicate rule retains at least {required}.";
            return false;
        }

        message = "All explicit duplicate retention floors remain satisfied.";
        return true;
    }

    private static int Count(IEnumerable<EquipmentInstanceSnapshot> instances, SquireDuplicateRetentionRule rule) =>
        instances.Count(instance =>
            instance.Fingerprint.ItemId == rule.ItemId &&
            instance.Fingerprint.IsHighQuality == rule.IsHighQuality);
}
