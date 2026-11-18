using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

/// <summary>What the cleanup predicate needs to know about one saved job and its surroundings.</summary>
/// <param name="Status">The job's resolved handoff status.</param>
/// <param name="IsSender">True when the logged-in character split this share off.</param>
/// <param name="Requirements">The job's material requirements; empty when the catalog could not resolve them.</param>
/// <param name="CountTheirInventory">The handover setting that subtracts the assembler's holdings.</param>
/// <param name="IncludeTheirRetainers">The handover setting that adds the assembler's retainer holdings.</param>
/// <param name="ActiveJobId">The saved job currently loaded in the prep queue, if any.</param>
/// <param name="PrepQueueCount">Lines in the active prep queue.</param>
/// <param name="TradeQueueBoundShareId">The share the Trade Queue is bound to, if any.</param>
/// <param name="HandoffBusy">True while a Workshop Host call or delivery for this job is in flight.</param>
public sealed record WorkshopMarketRunJobFacts(
    WorkshopFrozenQueue Job,
    WorkshopSavedJobHandoffStatus Status,
    bool IsSender,
    IReadOnlyList<WorkshopMaterialRequirement> Requirements,
    bool CountTheirInventory,
    bool IncludeTheirRetainers,
    Guid? ActiveJobId,
    int PrepQueueCount,
    Guid? TradeQueueBoundShareId,
    bool HandoffBusy);

/// <summary>
/// Decides when a market-run saved job is finished for this character and may be removed automatically.
/// Only a share this character sent can be proven finished: accepted, every owed unit handed over and
/// relayed, and nothing still trading. Anything else stays until the user deletes it.
/// </summary>
public static class WorkshopMarketRunCleanup
{
    /// <returns>Why the job counts as finished, or null when it must stay.</returns>
    public static string? FinishedReason(WorkshopMarketRunJobFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var job = facts.Job;
        if (job.Origin != WorkshopSavedJobOrigin.MarketRun)
            return null;
        if (facts.ActiveJobId == job.Id && facts.PrepQueueCount > 0)
            return null;

        // Jobs this character assembles (or received) have no record that proves every project was built.
        if (!WorkshopSavedJobHandoff.IsHandoff(job) || !facts.IsSender)
            return null;
        if (facts.Status != WorkshopSavedJobHandoffStatus.Accepted || facts.HandoffBusy)
            return null;
        if (job.HandoverInFlight != null || job.HandoverReportPending || job.HandoverReportRefusal != null)
            return null;
        if (facts.TradeQueueBoundShareId == job.Id)
            return null;
        if (facts.Requirements.Count == 0)
            return null;

        // Only the frozen baseline is stable without the host; it is frozen once any unit is handed over.
        if (!job.HandedOver.Any(entry => entry.Quantity > 0))
            return null;

        var theyHold = WorkshopHandoverCalculator.CombineHoldings(
            facts.Requirements,
            Ledger(job.HandoverBaseline),
            Ledger(job.HandoverRetainerBaseline),
            facts.IncludeTheirRetainers);
        var rows = WorkshopHandoverCalculator.Build(facts.Requirements, job.HandedOver, theyHold, new Dictionary<uint, int>());
        if (rows.Any(row => row.Owed(facts.CountTheirInventory) > 0))
            return null;

        var due = WorkshopHandoverCalculator.HandoverTotal(rows, facts.CountTheirInventory);
        return $"accepted by {job.HandoffTargetName}; all {due:N0} owed unit(s) handed over and reported to the Workshop Host";
    }

    private static IReadOnlyDictionary<uint, int>? Ledger(IReadOnlyList<WorkshopMaterialLedgerEntry>? entries) =>
        entries?
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Quantity));
}
