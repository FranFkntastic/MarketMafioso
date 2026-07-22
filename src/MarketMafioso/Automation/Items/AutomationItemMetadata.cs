using System.Collections.Generic;
using Franthropy.FFXIV.Filtering;

namespace MarketMafioso.Automation.Items;

public sealed record AutomationItemJob(
    FfxivJobKey Key,
    string Name,
    string Abbreviation);

public sealed record AutomationItemMetadata(
    AutomationItemIdentity Identity,
    int MaxStack,
    string? ItemType = null,
    bool SupportsCondition = false,
    long? ItemLevel = null,
    long? EquipLevel = null,
    IReadOnlyList<AutomationItemJob>? EligibleJobs = null,
    IReadOnlyCollection<FfxivEquipmentSlot>? Slots = null,
    FfxivItemRarity? Rarity = null,
    FfxivUiCategoryKey? UiCategory = null,
    string? UiCategoryName = null,
    bool? IsUnique = null,
    bool? IsTradable = null,
    bool? IsDesynthesizable = null,
    bool? IsHighQualityCapable = null,
    long? DefinitionMaxStackSize = null);
