using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record MinerBotanistAdvisorCatalogResult(
    string CoverageLabel,
    IReadOnlyList<uint> MarketItemIds,
    IReadOnlyList<EquipmentLoadoutOffer> VendorOffers,
    IReadOnlyDictionary<uint, EquipmentItemDefinition> Definitions);

/// <summary>
/// Static, patch-matched discovery for the first advisor release. It declares a bounded level
/// horizon and never reads character, inventory, agent, or gearset state.
/// </summary>
public sealed class MinerBotanistAdvisorCatalog
{
    public const uint MinimumEquipLevel = 90;
    public const uint MaximumEquipLevel = 100;

    private readonly IDataManager dataManager;
    private readonly LuminaRenderedEquipmentDefinitionLookup definitions;
    private readonly OutfitterGilVendorCatalog vendors;
    private readonly Dictionary<uint, MinerBotanistAdvisorCatalogResult> byClassJobId = [];

    public MinerBotanistAdvisorCatalog(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        definitions = new(dataManager);
        vendors = new(dataManager);
    }

    public MinerBotanistAdvisorCatalogResult Build(uint classJobId)
    {
        if (classJobId is not (MinerBotanistUtilityProfile.MinerClassJobId or MinerBotanistUtilityProfile.BotanistClassJobId))
            throw new ArgumentOutOfRangeException(nameof(classJobId), "The first advisor catalog supports MIN and BTN only.");
        if (byClassJobId.TryGetValue(classJobId, out var cached))
            return cached;

        var itemSheet = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet is unavailable.");
        var found = new Dictionary<uint, EquipmentItemDefinition>();
        var marketItemIds = new List<uint>();
        var vendorOffers = new List<EquipmentLoadoutOffer>();
        foreach (var item in itemSheet.Where(value =>
                     value.RowId > 0 &&
                     value.LevelEquip >= MinimumEquipLevel &&
                     value.LevelEquip <= MaximumEquipLevel &&
                     value.EquipSlotCategory.RowId != 0))
        {
            var definition = definitions.FindByItemId(item.RowId).SingleOrDefault();
            if (definition is null || !definition.EligibleClassJobIds.Contains(classJobId) ||
                !HasRelevantCompleteProfile(definition) || HasUnmodeledEffectOrRestriction(definition))
                continue;
            found[definition.ItemId] = definition;

            if (!item.IsUntradable && item.ItemSearchCategory.RowId != 0)
                marketItemIds.Add(definition.ItemId);

            var vendor = vendors.FindOffers(definition.ItemId)
                .OrderBy(value => value.UnitPriceGil)
                .ThenBy(value => value.SourceLabel, StringComparer.Ordinal)
                .FirstOrDefault();
            if (vendor is not null)
                vendorOffers.Add(new(
                    definition,
                    EquipmentAcquisitionSourceKind.GilVendor,
                    vendor.SourceLabel,
                    vendor.UnitPriceGil,
                    Quality: EquipmentQuality.Normal,
                    SourceCatalogKey: $"vendor:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}:{definition.ItemId}"));
        }

        var result = new MinerBotanistAdvisorCatalogResult(
            $"Equipped UI evidence plus level {MinimumEquipLevel}-{MaximumEquipLevel} MIN/BTN market and gil-vendor equipment; armoury inventory is not yet observed.",
            marketItemIds.Distinct().Order().ToArray(),
            vendorOffers,
            found);
        byClassJobId[classJobId] = result;
        return result;
    }

    private static bool HasRelevantCompleteProfile(EquipmentItemDefinition definition) =>
        Relevant(definition.StatProfile) || Relevant(definition.HighQualityStatProfile);

    private static bool Relevant(EquipmentStatProfile? profile) =>
        profile is { IsComplete: true } && profile.Parameters.Any(value => value.Value > 0 && value.Semantic is
            EquipmentStatSemantic.Gathering or EquipmentStatSemantic.Perception or EquipmentStatSemantic.GatheringPoints);

    private static bool HasUnmodeledEffectOrRestriction(EquipmentItemDefinition definition) =>
        definition.ItemSpecialBonusId != 0 || definition.ItemActionId != 0 || definition.HasUnmodeledEquipRestriction;
}
