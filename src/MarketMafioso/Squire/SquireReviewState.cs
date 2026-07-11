using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed class SquireReviewState
{
    private Guid? generationId;
    private readonly Dictionary<EquipmentInstanceFingerprint, SquireDisposition> selections = new();

    public Guid? GenerationId => generationId;
    public IReadOnlyDictionary<EquipmentInstanceFingerprint, SquireDisposition> Selections => selections;

    public void Adopt(SquireAnalysis analysis)
    {
        generationId = analysis.Snapshot.GenerationId;
        selections.Clear();
    }

    public bool TrySelect(SquireAnalysis analysis, EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
    {
        if (generationId != analysis.Snapshot.GenerationId)
            return false;
        var candidate = analysis.Candidates.FirstOrDefault(value => value.Instance.Fingerprint == fingerprint);
        if (candidate is null || !candidate.IsExecutable || !candidate.SupportedDispositions.Contains(disposition))
            return false;
        selections[fingerprint] = disposition;
        return true;
    }

    public void Clear() => selections.Clear();

    public void Invalidate()
    {
        generationId = null;
        selections.Clear();
    }
}
