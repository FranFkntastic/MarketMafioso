namespace MarketMafioso.WorkshopPrep;

// The Free Company's unlocked workshop schematics, as the company crafting log
// agent holds them: one bit per CompanyCraftDraft row (bit n = draft row n).
// Projects whose sequence names no draft are always craftable.
public readonly record struct WorkshopDraftUnlocks(ulong Mask)
{
    // Lumina reports a sequence without a draft as row 0xFFFFFFFF.
    public const uint NoDraft = uint.MaxValue;

    // Drafts run to row 38 in 7.x. Anything at or past this bound is far more
    // likely a misread than a real unlock, so such a mask is rejected outright.
    public const int MaxPlausibleDraftRows = 48;

    // True/false when the mask answers for the draft; null when it cannot.
    public bool? IsUnlocked(uint draftId)
    {
        if (draftId == NoDraft)
            return true;
        if (draftId >= 64)
            return null;
        return (Mask & (1UL << (int)draftId)) != 0;
    }

    // The agent field is trusted only when the addon mirrors its low dword and
    // no bit falls outside the draft sheet.
    public static WorkshopDraftUnlocks? TryCreate(ulong agentMask, uint? addonLowWord, int draftRowLimit = MaxPlausibleDraftRows)
    {
        if (addonLowWord == null || (uint)agentMask != addonLowWord.Value)
            return null;
        if (draftRowLimit < 64 && agentMask >> draftRowLimit != 0)
            return null;
        return new WorkshopDraftUnlocks(agentMask);
    }
}
