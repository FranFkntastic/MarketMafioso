using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.MarketAcquisition.ExactAuthority;

public sealed record ExactAcquisitionWorkbenchEvidenceLineage(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region,
    string CoverageMode,
    DateTimeOffset PublishedAtUtc);

public sealed record ExactAcquisitionWorkbenchMarketLot(
    EquipmentOfferKey OfferKey,
    string ItemName,
    uint RequiredQuantity,
    uint ObservedAvailableQuantity,
    string WorldName,
    uint ObservedUnitPriceGil,
    ulong ObservedTotalPriceGil,
    string DiscoveryObservationId,
    string SourceRevision,
    DateTimeOffset ReviewedAtUtc,
    string? RetainerName = null,
    string? RetainerId = null,
    string ItemKind = "Equipment");

public sealed record ExactAcquisitionWorkbenchSelectionLineage(
    EquipmentLoadoutPosition Position,
    EquipmentOfferKey OfferKey,
    uint Quantity,
    string? ObservationId,
    string SourceLabel);

/// <summary>
/// Product-neutral, versioned handoff from an external planning plugin into Market Acquisition.
/// The observed rows remain evidence; finalization creates the actual bounded authority contract.
/// </summary>
public sealed record ExactAcquisitionWorkbenchTransfer(
    string SchemaVersion,
    string Origin,
    string SelectedSolutionId,
    string? AdvisorNominationSolutionId,
    EquipmentUtilityProfileKey Profile,
    EquipmentUtilityContext Context,
    ExactAcquisitionWorkbenchEvidenceLineage Evidence,
    IReadOnlyList<ExactAcquisitionWorkbenchSelectionLineage> SelectedLoadout,
    IReadOnlyList<ExactAcquisitionWorkbenchMarketLot> MarketLots,
    ulong ObservedMarketTotalGil,
    bool DryRunOnly = false)
{
    public const string CurrentSchemaVersion = "gooseworks-exact-acquisition-workbench-transfer/v1";
    public const string ExternalPlanningOrigin = "ExternalPlanningPlugin";
}
