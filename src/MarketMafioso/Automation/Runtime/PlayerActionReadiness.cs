using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Automation.Runtime;

/// <summary>
/// Whether the logged-in character can be handed an action now. Commands and retainer work sent while it is zoning,
/// in a cutscene or held by an event are swallowed or refused before anything happens, so callers wait instead.
/// </summary>
internal static class PlayerActionReadiness
{
    private static readonly (ConditionFlag[] Flags, string Reason)[] Guards =
    [
        ([ConditionFlag.LoggingOut], "the character is logging out"),
        ([ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51], "the character is between areas"),
        ([ConditionFlag.ReadyingVisitOtherWorld, ConditionFlag.WaitingToVisitOtherWorld], "the character is travelling to another world"),
        ([ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78], "a cutscene is playing"),
        ([ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent], "the character is occupied in an event"),
    ];

    /// <summary>Why the character cannot act yet, or null when it can.</summary>
    public static string? Blocker(ICondition condition)
    {
        foreach (var (flags, reason) in Guards)
        {
            if (flags.Any(flag => condition[flag]))
                return reason;
        }
        return null;
    }
}
