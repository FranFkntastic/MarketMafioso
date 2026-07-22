#if DEBUG
using System;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.MarketAcquisition.ExactAuthority;

public enum ExactAcquisitionDryRunSeedStatus
{
    Seeded,
    AlreadySeeded,
    Rejected,
    PersistenceFailed,
}

public sealed record ExactAcquisitionDryRunSeedResult(ExactAcquisitionDryRunSeedStatus Status, string Message)
{
    public bool IsSuccess => Status is ExactAcquisitionDryRunSeedStatus.Seeded or ExactAcquisitionDryRunSeedStatus.AlreadySeeded;
}

public static class ExactAcquisitionDryRunSunkStateSeeder
{
    public static ExactAcquisitionRouteSunkPurchase CreateSemanticSeed(
        ExactAcquisitionExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(plan);
        if (!contract.Transfer.DryRunOnly)
            throw new InvalidOperationException("DEBUG sunk-state seeding is restricted to permanently non-spending External plan contracts.");
        if (plan.Status != "Ready" || plan.PreparedAtUtc == default)
            throw new InvalidOperationException("DEBUG sunk-state seeding requires one current prepared route generation.");

        var plannedRows = plan.WorldBatches
            .SelectMany(batch => batch.ItemSubtasks.SelectMany(subtask => subtask.Listings.Select(listing =>
                new PlannedRow(batch, subtask, listing))))
            .ToArray();
        var candidate = plannedRows
            .FirstOrDefault(value =>
            {
                var claimLine = claim.Lines.SingleOrDefault(line => line.LineId == value.Subtask.LineId);
                var quality = value.Listing.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal;
                var contractLine = contract.Lines.SingleOrDefault(line => line.ItemId == value.Listing.ItemId && line.Quality == quality);
                var lineRows = plannedRows.Where(row => row.Subtask.LineId == value.Subtask.LineId).ToArray();
                return claimLine is not null && contractLine is not null && value.Listing.Quantity > 0 &&
                       ExactAcquisitionDryRunExecutionStateRestorer.MatchesFinalizedMarketLot(
                           contract,
                           value.Batch.WorldName,
                           value.Listing) &&
                       value.Listing.TotalGil == checked(value.Listing.Quantity * value.Listing.UnitPrice) &&
                       lineRows.Length >= 2 &&
                       lineRows.All(row => row.Listing.Quantity > 0 &&
                           row.Listing.TotalGil == checked(row.Listing.Quantity * row.Listing.UnitPrice)) &&
                       lineRows.Select(RowIdentity).Distinct(StringComparer.Ordinal).Count() == lineRows.Length &&
                       lineRows.Aggregate(0ul, (sum, row) => checked(sum + row.Listing.Quantity)) == contractLine.RequiredQuantity &&
                       lineRows.Any(other => RowIdentity(other) != RowIdentity(value));
            }) ?? throw new InvalidOperationException("No complete planned listing stack can be seeded while leaving another distinct full listing in the dry-run route.");
        var draft = new ExactAcquisitionRouteSunkPurchase
        {
            SchemaVersion = ExactAcquisitionRouteSunkPurchase.CurrentSchemaVersion,
            ReceiptId = string.Empty,
            ContractId = contract.ContractId,
            CanonicalIntentHash = contract.CanonicalIntentHash,
            WorkbenchDocumentId = document.LocalRequestId,
            WorkbenchRevision = document.LocalRevision,
            PlanRequestId = plan.RequestId,
            PlanPreparedAtUtc = plan.PreparedAtUtc,
            WorldName = candidate.Batch.WorldName,
            LineId = candidate.Subtask.LineId,
            ItemId = candidate.Listing.ItemId,
            Quality = candidate.Listing.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal,
            ListingId = candidate.Listing.ListingId,
            RetainerId = candidate.Listing.RetainerId,
            Quantity = candidate.Listing.Quantity,
            UnitPriceGil = candidate.Listing.UnitPrice,
            TotalGil = candidate.Listing.TotalGil,
        };
        return draft with { ReceiptId = ExactAcquisitionDryRunExecutionStateRestorer.ComputeReceiptId(draft) };

        static string RowIdentity(PlannedRow value) =>
            $"{value.Batch.WorldName.ToUpperInvariant()}|{value.Subtask.LineId}|{value.Listing.ListingId}|{value.Listing.RetainerId}";
    }

    public static ExactAcquisitionDryRunSeedResult Seed(
        IExactAcquisitionRouteExecutionStateStore store,
        ExactAcquisitionExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        MarketAcquisitionPlan plan,
        ExactAcquisitionRouteSunkPurchase seed,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            if (!contract.Transfer.DryRunOnly)
                throw new InvalidOperationException("DEBUG sunk-state seeding is restricted to permanently non-spending External plan contracts.");
            ExactAcquisitionDryRunExecutionStateRestorer.ValidateReceipt(contract, document, claim, plan, seed);
            var existing = store.Restore();
            if (existing is not null)
            {
                if (!ExactAcquisitionRouteAuthoritySession.IsRestoredStateSane(existing, contract, claim))
                    throw new InvalidOperationException("Existing persisted External plan state does not match the exact seed contract.");
                if (existing.SunkPurchases.Count == 1 && existing.SunkPurchases[0].ReceiptId == seed.ReceiptId)
                {
                    ExactAcquisitionDryRunExecutionStateRestorer.RestoreRemainingPlan(contract, document, claim, plan, existing);
                    return new(ExactAcquisitionDryRunSeedStatus.AlreadySeeded, "The exact DEBUG sunk purchase is already persisted; no quantity or gil was deducted again.");
                }
                if (existing.TotalSpentGil != 0 || existing.Lines.Any(line => line.PurchasedQuantity != 0 || line.SpentGil != 0) ||
                    existing.SunkPurchases.Count != 0)
                    throw new InvalidOperationException("Persisted External plan state already contains different sunk evidence; DEBUG seeding refused to merge or overwrite it.");
            }

            var session = ExactAcquisitionRouteAuthoritySession.Consume(contract, document, plan, claim);
            var lines = session.State.Lines.ToArray();
            var index = Array.FindIndex(lines, line => line.LineId == seed.LineId);
            if (index < 0)
                throw new InvalidOperationException("DEBUG sunk seed line is absent from the finalized route state.");
            lines[index] = lines[index] with
            {
                PurchasedQuantity = seed.Quantity,
                SpentGil = seed.TotalGil,
            };
            var seeded = session.State with
            {
                Phase = ExactAcquisitionRouteAuthorityPhase.Paused,
                Lines = lines,
                TotalSpentGil = seed.TotalGil,
                Message = "DEBUG-only sunk purchase seeded for the non-spending restart dry-run gate.",
                UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
                SunkPurchases = [seed],
            };
            ExactAcquisitionDryRunExecutionStateRestorer.RestoreRemainingPlan(contract, document, claim, plan, seeded);
            try
            {
                store.Save(seeded);
            }
            catch (Exception exception)
            {
                return new(ExactAcquisitionDryRunSeedStatus.PersistenceFailed, $"DEBUG sunk-state persistence failed: {exception.Message}");
            }
            return new(ExactAcquisitionDryRunSeedStatus.Seeded, "Seeded one exact sunk purchase through the persisted store; the remaining route is still dry-run only.");
        }
        catch (Exception exception)
        {
            return new(ExactAcquisitionDryRunSeedStatus.Rejected, $"DEBUG sunk-state seed rejected: {exception.Message}");
        }
    }

    private sealed record PlannedRow(
        MarketAcquisitionWorldBatch Batch,
        MarketAcquisitionWorldItemSubtask Subtask,
        MarketAcquisitionPlannedListing Listing);
}
#endif
