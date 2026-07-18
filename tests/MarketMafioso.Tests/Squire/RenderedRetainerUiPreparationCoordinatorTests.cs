using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedRetainerUiPreparationCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Begin_completes_without_commands_when_rendered_retainer_list_is_visible()
    {
        var commands = new List<string>();
        var result = new RenderedRetainerUiPreparationCoordinator().Begin(Start, true, true, command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, result.Status);
        Assert.Empty(commands);
    }

    [Fact]
    public void Workflow_travels_targets_interacts_and_completes_only_from_rendered_ui()
    {
        var commands = new List<string>();
        bool Process(string command) { commands.Add(command); return true; }
        var coordinator = new RenderedRetainerUiPreparationCoordinator();

        Assert.Equal(RenderedRetainerUiPreparationStatus.Traveling, coordinator.Begin(Start, false, true, Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Traveling, coordinator.Advance(Start.AddSeconds(1), false, true, false, false, false, true, false, "Summoning Bell", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.TargetingBell, coordinator.Advance(Start.AddSeconds(3), false, true, false, false, false, true, false, "Summoning Bell", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.ApproachingBell, coordinator.Advance(Start.AddSeconds(4), false, true, false, false, true, true, false, "Summoning Bell", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.ApproachingBell, coordinator.Advance(Start.AddSeconds(5), false, true, false, false, true, true, true, "Summoning Bell", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.WaitingForRetainerList, coordinator.Advance(Start.AddSeconds(6), false, true, false, false, true, true, false, "Summoning Bell", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, coordinator.Advance(Start.AddSeconds(7), true, true, false, false, true, true, false, "Summoning Bell", Process).Status);
        Assert.Equal(["/li mb", "rendered-ui:rollover-nameplate:Summoning Bell", "/vnav movetarget", "/confirm"], commands);
    }

    [Fact]
    public void Workflow_fails_closed_when_lifestream_state_is_unavailable()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, _ => true);

        var result = coordinator.Advance(Start.AddSeconds(3), false, false, false, false, false, true, false, "Summoning Bell", _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
    }

    [Fact]
    public void Workflow_bounds_bell_retries_when_no_rendered_list_appears()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, _ => true);
        coordinator.Advance(Start.AddSeconds(3), false, true, false, false, false, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(4), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(6), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(10), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(11), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(13), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(17), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(18), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(20), false, true, false, false, true, true, false, "Summoning Bell", _ => true);
        coordinator.Advance(Start.AddSeconds(24), false, true, false, false, true, true, false, "Summoning Bell", _ => true);

        var result = coordinator.Advance(Start.AddSeconds(25), false, true, false, false, true, true, false, "Summoning Bell", _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
        Assert.Equal(3, result.InteractionAttempts);
    }

    [Fact]
    public void Rendered_market_board_arrival_overrides_lifestream_busy_state()
    {
        var commands = new List<string>();
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, command => { commands.Add(command); return true; });

        var result = coordinator.Advance(
            Start.AddSeconds(3),
            false,
            true,
            true,
            true,
            false,
            true,
            false,
            "Summoning Bell",
            command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.TargetingBell, result.Status);
        Assert.Equal(["/li mb", "rendered-ui:rollover-nameplate:Summoning Bell"], commands);
    }

    [Fact]
    public void Workflow_fails_closed_when_rendered_target_does_not_confirm_bell()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, _ => true);
        coordinator.Advance(Start.AddSeconds(3), false, true, false, true, false, true, false, "Summoning Bell", _ => true);

        var result = coordinator.Advance(Start.AddSeconds(6), false, true, false, false, false, true, false, "Summoning Bell", _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
        Assert.Contains("rendered target bar", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
