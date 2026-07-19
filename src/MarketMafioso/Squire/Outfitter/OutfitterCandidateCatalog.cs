using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter;

public sealed class OutfitterCandidateCatalog
{
    private readonly IDataManager dataManager;
    private readonly OutfitterGilVendorCatalog gilVendors;
    private readonly Dictionary<uint, IReadOnlyList<PurchasableSeed>> purchasableByJob = new();

    public OutfitterCandidateCatalog(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        gilVendors = new(dataManager);
    }

    public IReadOnlyList<EquipmentLoadoutOffer> BuildOffers(
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target,
        uint targetLevel,
        IReadOnlyDictionary<uint, OutfitterMarketQuote> marketQuotes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(marketQuotes);
        if (target.Job is null)
            return [];

        var offers = new List<EquipmentLoadoutOffer>();
        foreach (var instance in snapshot.Instances)
        {
            if (!snapshot.Definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition))
                continue;
            var owned = new EquipmentLoadoutOffer(
                definition,
                EquipmentAcquisitionSourceKind.Owned,
                FormatOwnedSource(instance),
                Instance: instance);
            if (OutfitterCandidateEligibility.IsRecommendable(target.Job, owned))
                offers.Add(owned);
        }

        foreach (var seed in GetPurchasableSeeds(target.Job)
                     .Where(seed => seed.Definition.EquipLevel <= targetLevel))
        {
            if (seed.VendorOffer is { } vendor)
            {
                var vendorCandidate = new EquipmentLoadoutOffer(
                    seed.Definition,
                    EquipmentAcquisitionSourceKind.GilVendor,
                    vendor.SourceLabel,
                    vendor.UnitPriceGil);
                if (OutfitterCandidateEligibility.IsRecommendable(target.Job, vendorCandidate))
                    offers.Add(vendorCandidate);
            }
            if (seed.IsMarketable)
            {
                marketQuotes.TryGetValue(seed.Definition.ItemId, out var quote);
                var marketCandidate = new EquipmentLoadoutOffer(
                    seed.Definition,
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    quote is null ? "Market board · quote needed" : $"Market board · {quote.WorldName}",
                    quote?.UnitPriceGil,
                    PriceIsEstimate: true);
                if (OutfitterCandidateEligibility.IsRecommendable(target.Job, marketCandidate))
                    offers.Add(marketCandidate);
            }
        }
        return offers;
    }

    public IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer> BuildCurrentItems(
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target) => BuildCurrentItemsCore(snapshot, target);

    internal static IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer> BuildCurrentItemsCore(
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target)
    {
        var current = new Dictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer>();
        if (target.Gearset is null)
            return current;

        var ringIndex = 0;
        foreach (var item in target.Gearset.Items)
        {
            if (!snapshot.Definitions.TryGetValue(item.ItemId, out var definition))
                continue;
            var position = ToPosition(item.Slot, ringIndex);
            if (position is null)
                continue;
            if (item.Slot == EquipmentSlot.Ring)
                ringIndex++;
            var instance = snapshot.Instances.FirstOrDefault(value =>
                value.Fingerprint.ItemId == item.ItemId &&
                (item.IsHighQuality is null || value.Fingerprint.IsHighQuality == item.IsHighQuality));
            current[position.Value] = new(
                definition,
                EquipmentAcquisitionSourceKind.Owned,
                target.Gearset.Name,
                Instance: instance);
        }
        return current;
    }

    private IReadOnlyList<PurchasableSeed> GetPurchasableSeeds(CharacterJobSnapshot job)
    {
        if (purchasableByJob.TryGetValue(job.ClassJobId, out var cached))
            return cached;

        var itemSheet = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet unavailable.");
        var baseParamSheet = dataManager.GetExcelSheet<BaseParam>() ?? throw new InvalidOperationException("BaseParam sheet unavailable.");
        var values = new List<PurchasableSeed>();
        foreach (var value in itemSheet)
        {
            var slot = DalamudCharacterEquipmentSnapshotSource.MapEquipSlot(value.EquipSlotCategory.RowId);
            if (slot is EquipmentSlot.Unknown or EquipmentSlot.SoulCrystal ||
                value.RowId == 0 || string.IsNullOrWhiteSpace(value.Name.ToString()) ||
                !DalamudCharacterEquipmentSnapshotSource.IsEligible(value.ClassJobCategory.Value, job.Abbreviation))
                continue;

            var parameters = new List<EquipmentStatValue>();
            for (var index = 0; index < value.BaseParam.Count; index++)
            {
                var baseParamId = value.BaseParam[index].RowId;
                var amount = value.BaseParamValue[index];
                if (baseParamId == 0 || amount <= 0)
                    continue;
                var name = baseParamSheet.GetRowOrDefault(baseParamId)?.Name.ToString();
                parameters.Add(new(
                    baseParamId,
                    DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(baseParamId, name),
                    amount,
                    false,
                    name));
            }
            if (!OutfitterCandidateEligibility.HasRelevantStats(job, slot, value.DamagePhys, value.DamageMag, parameters))
                continue;

            if (job.Discipline == EquipmentDiscipline.Combat &&
                slot is EquipmentSlot.Head or EquipmentSlot.Body or EquipmentSlot.Hands or EquipmentSlot.Legs or EquipmentSlot.Feet &&
                value.ClassJobCategory.RowId == 1)
                continue;

            var marketable = !value.IsUntradable && value.ItemSearchCategory.RowId != 0;
            var vendorOffer = gilVendors.FindOffers(value.RowId).FirstOrDefault();
            if (!marketable && vendorOffer is null)
                continue;

            var slotCategory = value.EquipSlotCategory.Value;
            var profile = new EquipmentStatProfile(
                parameters,
                value.DamagePhys,
                value.DamageMag,
                value.DefensePhys,
                value.DefenseMag,
                parameters.All(parameter => parameter.Semantic != EquipmentStatSemantic.Unknown));
            var definition = new EquipmentItemDefinition(
                value.RowId,
                value.Name.ToString(),
                value.LevelEquip,
                value.LevelItem.RowId,
                slot,
                new HashSet<uint> { job.ClassJobId },
                value.Rarity,
                true,
                false,
                value.Desynth > 0,
                value.PriceLow > 0 && !value.IsIndisposable,
                value.PriceLow,
                !value.IsIndisposable,
                null,
                null,
                value.IsUnique && value.IsUntradable && value.Rarity >= 4,
                profile,
                NormalizedRarity: DalamudCharacterEquipmentSnapshotSource.MapRarity(value.Rarity),
                IsUnique: value.IsUnique,
                EquipSlotCategoryId: value.EquipSlotCategory.RowId,
                MainHandOccupancy: slotCategory.MainHand,
                OffHandOccupancy: slotCategory.OffHand,
                FitsLeftRing: slotCategory.FingerL != 0,
                FitsRightRing: slotCategory.FingerR != 0,
                IsAllClasses: value.ClassJobCategory.RowId == 1,
                ClassJobCategoryId: value.ClassJobCategory.RowId,
                ClassJobCategoryName: value.ClassJobCategory.Value.Name.ToString(),
                ItemUiCategoryId: value.ItemUICategory.RowId,
                ItemUiCategoryName: value.ItemUICategory.Value.Name.ToString(),
                ItemSearchCategoryId: value.ItemSearchCategory.RowId,
                ItemSearchCategoryName: value.ItemSearchCategory.Value.Name.ToString());
            values.Add(new(definition, vendorOffer, marketable));
        }

        cached = values;
        purchasableByJob[job.ClassJobId] = cached;
        return cached;
    }

    private static string FormatOwnedSource(EquipmentInstanceSnapshot instance) =>
        instance.IsEquipped ? "Currently equipped" : instance.Fingerprint.Container.Replace("Armory", "Armoury ", StringComparison.Ordinal);

    private static EquipmentLoadoutPosition? ToPosition(EquipmentSlot slot, int ringIndex) => slot switch
    {
        EquipmentSlot.MainHand => EquipmentLoadoutPosition.MainHand,
        EquipmentSlot.OffHand => EquipmentLoadoutPosition.OffHand,
        EquipmentSlot.Head => EquipmentLoadoutPosition.Head,
        EquipmentSlot.Body => EquipmentLoadoutPosition.Body,
        EquipmentSlot.Hands => EquipmentLoadoutPosition.Hands,
        EquipmentSlot.Legs => EquipmentLoadoutPosition.Legs,
        EquipmentSlot.Feet => EquipmentLoadoutPosition.Feet,
        EquipmentSlot.Ears => EquipmentLoadoutPosition.Ears,
        EquipmentSlot.Neck => EquipmentLoadoutPosition.Neck,
        EquipmentSlot.Wrists => EquipmentLoadoutPosition.Wrists,
        EquipmentSlot.Ring when ringIndex == 0 => EquipmentLoadoutPosition.LeftRing,
        EquipmentSlot.Ring => EquipmentLoadoutPosition.RightRing,
        _ => null,
    };

    private sealed record PurchasableSeed(
        EquipmentItemDefinition Definition,
        OutfitterGilVendorOffer? VendorOffer,
        bool IsMarketable);
}
