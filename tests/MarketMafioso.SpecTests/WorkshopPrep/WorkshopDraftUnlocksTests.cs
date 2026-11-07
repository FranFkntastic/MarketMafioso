using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopDraftUnlocksTests
{
    // Eriana's FC (Secondary), read live on Nov 7: wheels, airship and the first
    // three submersible prototypes. Bit n is CompanyCraftDraft row n.
    private const ulong SecondaryMask = 0x17000703;

    [Theory]
    [InlineData(0u, true)]   // airship prototype II (row 0 is a real draft)
    [InlineData(8u, true)]   // aetherial wheel II
    [InlineData(24u, true)]  // submersible prototype I (Shark class)
    [InlineData(27u, false)]
    [InlineData(29u, false)] // submersible prototype V
    [InlineData(38u, false)]
    public void Bit_index_is_the_draft_row(uint draftId, bool expected)
    {
        var unlocks = WorkshopDraftUnlocks.TryCreate(SecondaryMask, (uint)SecondaryMask);

        Assert.NotNull(unlocks);
        Assert.Equal(expected, unlocks.Value.IsUnlocked(draftId));
    }

    [Fact]
    public void Projects_without_a_draft_are_always_craftable()
    {
        var none = WorkshopDraftUnlocks.TryCreate(0, 0);

        Assert.NotNull(none);
        Assert.True(none.Value.IsUnlocked(WorkshopDraftUnlocks.NoDraft));
        Assert.False(none.Value.IsUnlocked(24));
    }

    [Fact]
    public void Agent_mask_that_disagrees_with_the_addon_mirror_is_rejected()
    {
        Assert.Null(WorkshopDraftUnlocks.TryCreate(SecondaryMask, 0));
        Assert.Null(WorkshopDraftUnlocks.TryCreate(SecondaryMask, null));
    }

    [Fact]
    public void Bits_past_the_draft_sheet_are_treated_as_a_misread()
    {
        const ulong garbage = 0xFFFF_0000_0000_0000UL | SecondaryMask;

        Assert.Null(WorkshopDraftUnlocks.TryCreate(garbage, (uint)SecondaryMask));
    }

    [Fact]
    public void Draft_ids_the_mask_cannot_hold_are_unknown_not_locked()
    {
        var unlocks = new WorkshopDraftUnlocks(ulong.MaxValue);

        Assert.Null(unlocks.IsUnlocked(64));
    }

    [Fact]
    public void Only_the_idle_station_menu_is_dismissed_at_the_end_of_a_run()
    {
        Assert.True(WorkshopAssemblyPromptPolicy.IsIdleStationMenu(["View company crafting log.", "Nothing."]));
        Assert.False(WorkshopAssemblyPromptPolicy.IsIdleStationMenu(["Contribute materials.", "View company crafting log.", "Nothing."]));
        Assert.False(WorkshopAssemblyPromptPolicy.IsIdleStationMenu(["Nothing."]));
    }

    [Fact]
    public void Category_crash_names_the_missing_schematic_first()
    {
        var category = new WorkshopNativeFault(1, "crafting-log.category 1/18", []).Describe();
        var other = new WorkshopNativeFault(1, "crafting-log.project 530", []).Describe();

        Assert.StartsWith("The Free Company probably hasn't unlocked the schematic", category);
        Assert.StartsWith("The game crashed", other);
    }
}
