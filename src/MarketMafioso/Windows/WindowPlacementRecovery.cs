using System;
using System.Numerics;

namespace MarketMafioso.Windows;

internal static class WindowPlacementRecovery
{
    public static bool TryRecoverTitleBar(
        Vector2 position,
        Vector2 size,
        Vector2 workPosition,
        Vector2 workSize,
        float titleBarHeight,
        out Vector2 recoveredPosition)
    {
        recoveredPosition = position;
        if (!IsFinite(position) ||
            !IsFinite(size) ||
            !IsFinite(workPosition) ||
            !IsFinite(workSize) ||
            size.X <= 0f ||
            size.Y <= 0f ||
            workSize.X <= 0f ||
            workSize.Y <= 0f ||
            titleBarHeight <= 0f)
        {
            return false;
        }

        var workRight = workPosition.X + workSize.X;
        var workBottom = workPosition.Y + workSize.Y;
        var maxX = Math.Max(workPosition.X, workRight - size.X);
        var maxY = Math.Max(workPosition.Y, workBottom - titleBarHeight);
        recoveredPosition = new Vector2(
            Math.Clamp(position.X, workPosition.X, maxX),
            Math.Clamp(position.Y, workPosition.Y, maxY));
        return recoveredPosition != position;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
