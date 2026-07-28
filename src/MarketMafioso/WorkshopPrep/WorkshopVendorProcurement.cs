using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Vendors;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopVendorCandidate(
    GilVendorOffer Offer,
    GilVendorAccessAssessment Access);

public sealed record WorkshopMaterialProcurement(
    WorkshopMaterialAvailability Availability,
    int RetainerPlannedQuantity,
    int VendorNeed,
    IReadOnlyList<WorkshopVendorCandidate> Candidates,
    WorkshopVendorCandidate? SelectedCandidate,
    bool Selected,
    int ApprovedVendorQuantity)
{
    public ulong ApprovedGil =>
        SelectedCandidate is null
            ? 0
            : checked((ulong)ApprovedVendorQuantity * SelectedCandidate.Offer.UnitPriceGil);

    public bool CanBuyAutomatically =>
        SelectedCandidate is not null &&
        SelectedCandidate.Access.IsEligible &&
        VendorNeed > 0;
}

public sealed record WorkshopVendorStopReview(
    uint NpcId,
    uint ShopId,
    uint TerritoryId,
    string NpcName,
    IReadOnlyList<WorkshopMaterialProcurement> Lines)
{
    public ulong ApprovedGil => Lines.Aggregate(
        0ul,
        (sum, line) => checked(sum + line.ApprovedGil));
}

public sealed record WorkshopVendorRestockReview(
    string QueueSignature,
    IReadOnlyList<WorkshopMaterialProcurement> Materials,
    IReadOnlyList<WorkshopVendorStopReview> Stops)
{
    public int RetainerUnits => Materials.Sum(line => line.RetainerPlannedQuantity);
    public int VendorUnits => Materials.Where(line => line.Selected).Sum(line => line.ApprovedVendorQuantity);
    public ulong MaximumGil => Materials.Where(line => line.Selected).Aggregate(
        0ul,
        (sum, line) => checked(sum + line.ApprovedGil));
}

public sealed class WorkshopVendorProcurementPlanner
{
    private readonly GilVendorCatalog catalog;
    private readonly Func<GilVendorOffer, GilVendorAccessAssessment> assessAccess;

    public WorkshopVendorProcurementPlanner(
        GilVendorCatalog catalog,
        Func<GilVendorOffer, GilVendorAccessAssessment> assessAccess)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.assessAccess = assessAccess ?? throw new ArgumentNullException(nameof(assessAccess));
    }

    public WorkshopVendorRestockReview Build(
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        IReadOnlyDictionary<uint, int> approvedQuantities,
        IReadOnlySet<uint> excludedItems)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(approvedQuantities);
        ArgumentNullException.ThrowIfNull(excludedItems);

        var drafts = availability
            .Select(item =>
            {
                var retainerPlanned = Math.Min(item.Shortage, item.QuartermasterStock);
                var vendorNeed = Math.Max(0, item.Shortage - retainerPlanned);
                var candidates = catalog.FindOffers(item.ItemId)
                    .Select(offer => new WorkshopVendorCandidate(offer, assessAccess(offer)))
                    .OrderByDescending(candidate => candidate.Access.State == GilVendorAccessState.Verified)
                    .ThenByDescending(candidate => candidate.Access.State == GilVendorAccessState.Probeable)
                    .ThenBy(candidate => candidate.Offer.UnitPriceGil)
                    .ThenBy(candidate => candidate.Offer.NpcName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Offer.NpcId)
                    .ToArray();
                return new Draft(item, retainerPlanned, vendorNeed, candidates);
            })
            .ToArray();

        var selectedCandidates = SelectStops(drafts);
        var materials = drafts.Select(draft =>
        {
            selectedCandidates.TryGetValue(draft.Availability.ItemId, out var candidate);
            var selected = candidate is not null &&
                           draft.VendorNeed > 0 &&
                           !excludedItems.Contains(draft.Availability.ItemId);
            var approved = approvedQuantities.TryGetValue(draft.Availability.ItemId, out var configured)
                ? Math.Clamp(configured, 0, draft.VendorNeed)
                : draft.VendorNeed;
            return new WorkshopMaterialProcurement(
                draft.Availability,
                draft.RetainerPlanned,
                draft.VendorNeed,
                draft.Candidates,
                candidate,
                selected,
                selected ? approved : 0);
        }).ToArray();

        var stops = materials
            .Where(line => line.SelectedCandidate is not null && line.Selected && line.ApprovedVendorQuantity > 0)
            .GroupBy(line => new
            {
                line.SelectedCandidate!.Offer.NpcId,
                line.SelectedCandidate.Offer.ShopId,
                line.SelectedCandidate.Offer.TerritoryId,
                line.SelectedCandidate.Offer.NpcName,
            })
            .Select(group => new WorkshopVendorStopReview(
                group.Key.NpcId,
                group.Key.ShopId,
                group.Key.TerritoryId,
                group.Key.NpcName,
                group.OrderBy(line => line.Availability.ItemName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(stop => stop.Lines.Count)
            .ThenBy(stop => stop.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stop => stop.NpcId)
            .ToArray();

        return new(BuildQueueSignature(availability), materials, stops);
    }

    private static Dictionary<uint, WorkshopVendorCandidate> SelectStops(IReadOnlyList<Draft> drafts)
    {
        var remaining = drafts
            .Where(draft => draft.VendorNeed > 0 && draft.Candidates.Any(candidate => candidate.Access.IsEligible))
            .ToDictionary(draft => draft.Availability.ItemId);
        var selected = new Dictionary<uint, WorkshopVendorCandidate>();
        while (remaining.Count > 0)
        {
            var best = remaining.Values
                .SelectMany(draft => draft.Candidates.Where(candidate => candidate.Access.IsEligible).Select(candidate => new
                {
                    Draft = draft,
                    Candidate = candidate,
                    Key = new VendorKey(
                        candidate.Offer.NpcId,
                        candidate.Offer.ShopId,
                        candidate.Offer.TerritoryId),
                }))
                .GroupBy(value => value.Key)
                .Select(group => new
                {
                    group.Key,
                    Coverage = group.Select(value => value.Draft.Availability.ItemId).Distinct().Count(),
                    Verified = group.Count(value => value.Candidate.Access.State == GilVendorAccessState.Verified),
                    Price = group.Aggregate(
                        0UL,
                        (total, value) => checked(
                            total +
                            ((ulong)value.Candidate.Offer.UnitPriceGil * (uint)value.Draft.VendorNeed))),
                })
                .OrderByDescending(group => group.Verified > 0)
                .ThenByDescending(group => group.Coverage)
                .ThenBy(group => group.Price)
                .ThenBy(group => group.Key.NpcId)
                .First();

            var covered = remaining.Values
                .Where(draft => draft.Candidates.Any(candidate =>
                    candidate.Access.IsEligible &&
                    candidate.Offer.NpcId == best.Key.NpcId &&
                    candidate.Offer.ShopId == best.Key.ShopId &&
                    candidate.Offer.TerritoryId == best.Key.TerritoryId))
                .ToArray();
            foreach (var draft in covered)
            {
                selected[draft.Availability.ItemId] = draft.Candidates
                    .Where(candidate =>
                        candidate.Access.IsEligible &&
                        candidate.Offer.NpcId == best.Key.NpcId &&
                        candidate.Offer.ShopId == best.Key.ShopId &&
                        candidate.Offer.TerritoryId == best.Key.TerritoryId)
                    .OrderByDescending(candidate => candidate.Access.State == GilVendorAccessState.Verified)
                    .First();
                remaining.Remove(draft.Availability.ItemId);
            }
        }

        return selected;
    }

    public static string BuildQueueSignature(IEnumerable<WorkshopMaterialAvailability> availability)
    {
        var canonical = string.Join(
            "|",
            availability
                .OrderBy(line => line.ItemId)
                .Select(line =>
                    $"{line.ItemId}:{line.Required}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record Draft(
        WorkshopMaterialAvailability Availability,
        int RetainerPlanned,
        int VendorNeed,
        IReadOnlyList<WorkshopVendorCandidate> Candidates);

    private sealed record VendorKey(uint NpcId, uint ShopId, uint TerritoryId);
}
