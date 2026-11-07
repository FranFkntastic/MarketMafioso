using System;
using System.Collections.Generic;
using System.Numerics;

namespace MarketMafioso.WorkshopPrep;

internal static class WorkshopFabricationStationSelection
{
    // A workshop can hold several stations, some on another floor. Pick the one
    // genuinely nearest the player (3D, so floors count) and nothing out of reach.
    internal static T? SelectNearest<T>(IEnumerable<T> candidates, Vector3 player, Func<T, Vector3> position,
        float maxDistance) where T : class
    {
        T? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Vector3.Distance(player, position(candidate));
            if (distance > maxDistance || distance >= nearestDistance)
                continue;
            nearest = candidate;
            nearestDistance = distance;
        }

        return nearest;
    }
}
