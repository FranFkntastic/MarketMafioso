using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopAssemblyActionGuardTests
{
    private const WorkshopAssemblyRunnerState State = WorkshopAssemblyRunnerState.OpeningProject;

    [Fact]
    public void Identical_action_is_allowed_three_times_then_halts()
    {
        var guard = new WorkshopAssemblyActionGuard();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxIdenticalActions; index++)
            guard.NoteAction(State, "Selected category.");

        var halt = Assert.Throws<WorkshopAssemblyHaltException>(() => guard.NoteAction(State, "Selected category."));
        Assert.Contains("Selected category.", halt.Message);
    }

    [Fact]
    public void Alternating_actions_are_capped_per_state()
    {
        var guard = new WorkshopAssemblyActionGuard();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxActionsPerState; index++)
            guard.NoteAction(State, index % 2 == 0 ? "a" : "b");

        Assert.Throws<WorkshopAssemblyHaltException>(() => guard.NoteAction(State, "c"));
    }

    [Fact]
    public void Entering_a_new_state_resets_the_action_counters()
    {
        var guard = new WorkshopAssemblyActionGuard();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxIdenticalActions; index++)
            guard.NoteAction(State, "same");

        guard.EnterState();
        guard.NoteAction(State, "same");
    }

    [Fact]
    public void Station_reopens_without_progress_halt_and_progress_resets_them()
    {
        var guard = new WorkshopAssemblyActionGuard();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxStationReopensWithoutProgress; index++)
            guard.NoteStationReopen("reopen");

        guard.NoteProgress();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxStationReopensWithoutProgress; index++)
            guard.NoteStationReopen("reopen");

        Assert.Throws<WorkshopAssemblyHaltException>(() => guard.NoteStationReopen("reopen"));
    }

    [Fact]
    public void Entering_a_state_does_not_forgive_station_reopens()
    {
        var guard = new WorkshopAssemblyActionGuard();
        for (var index = 0; index < WorkshopAssemblyActionGuard.MaxStationReopensWithoutProgress; index++)
        {
            guard.NoteStationReopen("reopen");
            guard.EnterState();
        }

        Assert.Throws<WorkshopAssemblyHaltException>(() => guard.NoteStationReopen("reopen"));
    }
}
