using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter;

public enum OutfitterTargetKind
{
    Job,
    Gearset,
    Retainer,
}

public sealed record OutfitterTarget(
    string Key,
    OutfitterTargetKind Kind,
    string Name,
    string Subtitle,
    CharacterJobSnapshot? Job = null,
    GearsetSnapshot? Gearset = null,
    CachedRetainer? Retainer = null,
    bool IsReady = true,
    string? Diagnostic = null);

public sealed record OutfitterMarketQuote(
    uint ItemId,
    uint UnitPriceGil,
    string WorldName,
    uint AvailableQuantity,
    DateTimeOffset ListingReviewedAtUtc);

public sealed record OutfitterPlanSnapshot(
    OutfitterTarget Target,
    EquipmentLoadoutPlan? Plan,
    IReadOnlyDictionary<uint, OutfitterMarketQuote> MarketQuotes,
    string Status);
