using System.Collections.Generic;

namespace MarketMafioso.Squire.Outfitter;

public enum OutfitterWorkspaceView
{
    Planner,
    Advisor,
}

public sealed record OutfitterWorkspaceViewOption(
    OutfitterWorkspaceView View,
    string Label,
    string Description,
    bool Selected);

public static class OutfitterWorkspaceViewPresenter
{
    public static IReadOnlyList<OutfitterWorkspaceViewOption> Build(bool advisorSelected) =>
    [
        new(
            OutfitterWorkspaceView.Planner,
            "Loadout planner",
            "Plan complete job and retainer loadouts",
            !advisorSelected),
        new(
            OutfitterWorkspaceView.Advisor,
            "Cost / utility advisor",
            "Compare exact-quality MIN/BTN alternatives",
            advisorSelected),
    ];
}
