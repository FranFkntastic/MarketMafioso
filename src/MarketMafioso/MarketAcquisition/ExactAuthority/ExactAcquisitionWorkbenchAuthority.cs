using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using Newtonsoft.Json;

namespace MarketMafioso.MarketAcquisition.ExactAuthority;

public enum ExactAcquisitionWorkbenchLineageState
{
    Valid,
    Invalidated,
}

public sealed record ExactAcquisitionWorkbenchLineEnvelope(
    uint ItemId,
    string ItemName,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    uint ObservedMaxUnitPriceGil,
    ulong ObservedTotalPriceGil,
    uint MaxUnitPriceGil,
    uint MaxTotalGil,
    string ItemKind = "Equipment");

public sealed record ExactAcquisitionExecutionContract(
    string SchemaVersion,
    string ContractId,
    string WorkbenchDocumentId,
    int WorkbenchRevision,
    string CanonicalIntentHash,
    string RecoveryPolicyId,
    string TargetCharacterName,
    string TargetWorld,
    string Region,
    string WorldMode,
    string SweepScope,
    IReadOnlyList<string> SweepDataCenters,
    IReadOnlyList<string> AuthorizedWorlds,
    ulong PlanCapGil,
    ExactAcquisitionWorkbenchTransfer Transfer,
    IReadOnlyList<ExactAcquisitionWorkbenchLineEnvelope> Lines,
    DateTimeOffset ConfirmedAtUtc)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-exactAcquisition-execution-contract/v2";
}

public sealed record ExactAcquisitionWorkbenchAuthority(
    string SchemaVersion,
    ExactAcquisitionWorkbenchTransfer Transfer,
    int PriceFlexPercent,
    ulong PlanCapGil,
    string RecoveryPolicyId,
    IReadOnlyList<ExactAcquisitionWorkbenchLineEnvelope> Lines,
    ExactAcquisitionWorkbenchLineageState LineageState,
    string? InvalidationReason,
    ExactAcquisitionExecutionContract? FinalizedContract)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-exactAcquisition-workbench-authority/v1";
    public const string CrossWorldExactQualityV1 = "CrossWorldExactQuality/v1";

    public bool IsLineageValid => LineageState == ExactAcquisitionWorkbenchLineageState.Valid;
}

public sealed record ExactAcquisitionWorkbenchAuthorityValidation(bool IsValid, string? Error)
{
    public static ExactAcquisitionWorkbenchAuthorityValidation Valid { get; } = new(true, null);
}

public static class ExactAcquisitionWorkbenchAuthorityService
{
    public const int MinimumPriceFlexPercent = 0;
    public const int MaximumPriceFlexPercent = 500;

    public static MarketAcquisitionRequestDocument Stage(
        MarketAcquisitionRequestDocument document,
        ExactAcquisitionWorkbenchTransfer transfer,
        int priceFlexPercent = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transfer);
        if (!string.Equals(transfer.SchemaVersion, ExactAcquisitionWorkbenchTransfer.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(transfer.Origin, ExactAcquisitionWorkbenchTransfer.ExternalPlanningOrigin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Workbench accepts only the current exact-quality External plan ExactAcquisition transfer.");
        }

        var envelopes = BuildEnvelopes(transfer, priceFlexPercent);
        var transferredItemIds = envelopes.Select(line => line.ItemId).ToHashSet();
        var lines = document.Lines
            .Where(line => !transferredItemIds.Contains(line.ItemId))
            .ToList();
        lines.AddRange(envelopes.Select(ToRequestLine));

        var authority = new ExactAcquisitionWorkbenchAuthority(
            ExactAcquisitionWorkbenchAuthority.CurrentSchemaVersion,
            transfer,
            ClampFlex(priceFlexPercent),
            ApplyFlex(transfer.ObservedMarketTotalGil, priceFlexPercent),
            ExactAcquisitionWorkbenchAuthority.CrossWorldExactQualityV1,
            envelopes,
            ExactAcquisitionWorkbenchLineageState.Valid,
            null,
            null);
        return MarkEdited(document, document with { Lines = lines, ExactAcquisitionAuthority = authority });
    }

    public static MarketAcquisitionRequestDocument UpdatePriceFlex(
        MarketAcquisitionRequestDocument document,
        int priceFlexPercent)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.ExactAcquisitionAuthority ??
            throw new InvalidOperationException("No External plan ExactAcquisition solution is attached to this Workbench.");
        if (!authority.IsLineageValid)
            throw new InvalidOperationException("Return to Advisor before changing caps on an invalidated solution.");

        var envelopes = BuildEnvelopes(authority.Transfer, priceFlexPercent);
        var replacements = envelopes.ToDictionary(LineKey);
        var lines = document.Lines.Select(line =>
        {
            var quality = QualityFromPolicy(line.HqPolicy);
            return quality is { } exact && replacements.TryGetValue((line.ItemId, exact), out var envelope)
                ? ToRequestLine(envelope)
                : line;
        }).ToList();
        var updatedAuthority = authority with
        {
            PriceFlexPercent = ClampFlex(priceFlexPercent),
            PlanCapGil = ApplyFlex(authority.Transfer.ObservedMarketTotalGil, priceFlexPercent),
            Lines = envelopes,
            FinalizedContract = null,
        };
        return MarkEdited(document, document with { Lines = lines, ExactAcquisitionAuthority = updatedAuthority });
    }

    public static MarketAcquisitionRequestDocument ReconcileEdit(
        MarketAcquisitionRequestDocument previous,
        MarketAcquisitionRequestDocument updated)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(updated);
        var authority = previous.ExactAcquisitionAuthority;
        if (authority is null)
            return updated;

        var validation = ValidateLineage(updated.Lines, authority);
        var reconciled = validation.IsValid
            ? authority with { FinalizedContract = null }
            : authority with
            {
                LineageState = ExactAcquisitionWorkbenchLineageState.Invalidated,
                InvalidationReason = validation.Error,
                FinalizedContract = null,
            };
        return updated with { ExactAcquisitionAuthority = reconciled };
    }

    public static ExactAcquisitionWorkbenchAuthorityValidation ValidateForFinalization(
        MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.ExactAcquisitionAuthority;
        if (authority is null)
            return ExactAcquisitionWorkbenchAuthorityValidation.Valid;
        if (!authority.IsLineageValid)
            return new(false, authority.InvalidationReason ?? "The selected external exact-acquisition solution changed; return to Advisor.");
        if (!string.Equals(authority.RecoveryPolicyId, ExactAcquisitionWorkbenchAuthority.CrossWorldExactQualityV1, StringComparison.Ordinal))
            return new(false, "The exact-acquisition recovery policy is not an approved version.");
        var lineage = ValidateLineage(document.Lines, authority);
        if (!lineage.IsValid)
            return lineage;
        foreach (var expected in authority.Lines)
        {
            var line = document.Lines.Single(value =>
                value.ItemId == expected.ItemId && QualityFromPolicy(value.HqPolicy) == expected.Quality);
            if (line.MaxUnitPrice != expected.MaxUnitPriceGil || line.GilCap != expected.MaxTotalGil)
                return new(false, $"{expected.ItemName} fixed ceilings no longer match the visible External plan approval envelope.");
        }
        if (authority.PlanCapGil == 0 || authority.Lines.Any(line => line.MaxUnitPriceGil == 0 || line.MaxTotalGil == 0))
            return new(false, "Every External plan line and the external exact-acquisition plan require a fixed gil ceiling.");
        return ExactAcquisitionWorkbenchAuthorityValidation.Valid;
    }

    public static MarketAcquisitionRequestDocument Finalize(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.ExactAcquisitionAuthority;
        if (authority is null)
            return document;
        var validation = ValidateForFinalization(document);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Error);

        var intentHash = ComputeCanonicalIntentHash(document);
        if (authority.FinalizedContract is { } existing &&
            existing.SchemaVersion == ExactAcquisitionExecutionContract.CurrentSchemaVersion &&
            existing.AuthorizedWorlds is { Count: > 0 } &&
            existing.WorkbenchRevision == document.LocalRevision &&
            string.Equals(existing.CanonicalIntentHash, intentHash, StringComparison.Ordinal))
        {
            return document;
        }

        var contract = new ExactAcquisitionExecutionContract(
            ExactAcquisitionExecutionContract.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            document.LocalRequestId,
            document.LocalRevision,
            intentHash,
            authority.RecoveryPolicyId,
            document.TargetCharacterName,
            document.TargetWorld,
            document.Region,
            document.WorldMode,
            document.SweepScope,
            document.SweepDataCenters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            ResolveAuthorizedWorlds(document),
            authority.PlanCapGil,
            authority.Transfer,
            authority.Lines,
            DateTimeOffset.UtcNow);
        return document with
        {
            ExactAcquisitionAuthority = authority with { FinalizedContract = contract },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static string ComputeCanonicalIntentHash(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authorityJson = document.ExactAcquisitionAuthority is null
            ? string.Empty
            : JsonConvert.SerializeObject(document.ExactAcquisitionAuthority with { FinalizedContract = null }, Formatting.None);
        var payload = $"{document.TargetCharacterName.Trim()}\n{document.TargetWorld.Trim()}\n{MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document)}\n{authorityJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool IsAuthorityLine(MarketAcquisitionRequestDocument document, int index)
    {
        if (document.ExactAcquisitionAuthority is not { } authority || index < 0 || index >= document.Lines.Count)
            return false;
        var line = document.Lines[index];
        var quality = QualityFromPolicy(line.HqPolicy);
        return quality is { } exact && authority.Lines.Any(value => value.ItemId == line.ItemId && value.Quality == exact);
    }

    private static ExactAcquisitionWorkbenchAuthorityValidation ValidateLineage(
        IReadOnlyList<MarketAcquisitionRequestLineDocument> requestLines,
        ExactAcquisitionWorkbenchAuthority authority)
    {
        foreach (var expected in authority.Lines)
        {
            var matches = requestLines.Where(line =>
                line.ItemId == expected.ItemId && QualityFromPolicy(line.HqPolicy) == expected.Quality).ToArray();
            if (matches.Length != 1)
                return new(false, $"{expected.ItemName} {QualityLabel(expected.Quality)} is missing or duplicated.");
            var line = matches[0];
            if (!line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase) ||
                line.TargetQuantity != expected.RequiredQuantity)
            {
                return new(false, $"{expected.ItemName} {QualityLabel(expected.Quality)} no longer matches the selected quantity.");
            }
        }
        return ExactAcquisitionWorkbenchAuthorityValidation.Valid;
    }

    private static IReadOnlyList<ExactAcquisitionWorkbenchLineEnvelope> BuildEnvelopes(
        ExactAcquisitionWorkbenchTransfer transfer,
        int priceFlexPercent) => transfer.MarketLots
        .GroupBy(lot => (lot.OfferKey.ItemId, lot.OfferKey.Quality))
        .Select(group => new ExactAcquisitionWorkbenchLineEnvelope(
            group.Key.ItemId,
            group.Select(lot => lot.ItemName).First(name => !string.IsNullOrWhiteSpace(name)),
            group.Key.Quality,
            group.Aggregate(0u, (sum, lot) => checked(sum + lot.RequiredQuantity)),
            group.Max(lot => lot.ObservedUnitPriceGil),
            group.Aggregate(0ul, (sum, lot) => checked(sum + lot.ObservedTotalPriceGil)),
            ApplyFlex(group.Max(lot => lot.ObservedUnitPriceGil), priceFlexPercent),
            ApplyFlexToUInt(group.Aggregate(0ul, (sum, lot) => checked(sum + lot.ObservedTotalPriceGil)), priceFlexPercent),
            group.Select(lot => lot.ItemKind).Distinct(StringComparer.Ordinal).Single()))
        .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(line => line.ItemId)
        .ThenBy(line => line.Quality)
        .ToArray();

    private static MarketAcquisitionRequestLineDocument ToRequestLine(ExactAcquisitionWorkbenchLineEnvelope envelope) => new()
    {
        ItemId = envelope.ItemId,
        ItemName = envelope.ItemName,
        ItemKind = envelope.ItemKind,
        QuantityMode = "TargetQuantity",
        TargetQuantity = envelope.RequiredQuantity,
        MaxQuantity = 0,
        HqPolicy = envelope.Quality == EquipmentQuality.High ? "HQOnly" : "NQOnly",
        MaxUnitPrice = envelope.MaxUnitPriceGil,
        GilCap = envelope.MaxTotalGil,
    };

    private static int ClampFlex(int value) => Math.Clamp(value, MinimumPriceFlexPercent, MaximumPriceFlexPercent);

    private static uint ApplyFlex(uint value, int percent) => ApplyFlexToUInt(value, percent);

    private static ulong ApplyFlex(ulong value, int percent)
    {
        var factor = (ulong)(100 + ClampFlex(percent));
        return checked((value * factor + 99) / 100);
    }

    private static uint ApplyFlexToUInt(ulong value, int percent)
    {
        var adjusted = ApplyFlex(value, percent);
        if (adjusted > uint.MaxValue)
            throw new InvalidOperationException("The derived External plan gil ceiling exceeds the Workbench limit.");
        return (uint)adjusted;
    }

    private static (uint ItemId, EquipmentQuality Quality) LineKey(ExactAcquisitionWorkbenchLineEnvelope line) =>
        (line.ItemId, line.Quality);

    private static EquipmentQuality? QualityFromPolicy(string policy) => policy switch
    {
        "HQOnly" => EquipmentQuality.High,
        "NQOnly" => EquipmentQuality.Normal,
        _ => null,
    };

    private static string QualityLabel(EquipmentQuality quality) =>
        quality == EquipmentQuality.High ? "HQ" : "NQ";

    private static IReadOnlyList<string> ResolveAuthorizedWorlds(MarketAcquisitionRequestDocument document)
    {
        var dataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(document.Region);
        IEnumerable<string> worlds = document.WorldMode == "AllWorldSweep"
            ? document.SweepScope switch
            {
                "Region" => dataCenters.Values.SelectMany(value => value),
                "CurrentDataCenter" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    document.Region,
                    [MarketAcquisitionWorldCatalog.ResolveDataCenter(document.TargetWorld)]),
                "DataCenters" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    document.Region,
                    document.SweepDataCenters),
                _ => throw new InvalidOperationException($"Unknown all-world sweep scope {document.SweepScope}."),
            }
            : dataCenters.Values.SelectMany(value => value);
        return worlds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MarketAcquisitionRequestDocument MarkEdited(
        MarketAcquisitionRequestDocument previous,
        MarketAcquisitionRequestDocument updated) =>
        (updated with { LocalRevision = previous.LocalRevision }).WithNextRevision(
            string.IsNullOrWhiteSpace(previous.RemoteRequestId) ? "NewDraft" : "LocalEdits");
}

internal static class ExactAcquisitionWorkbenchAuthorityPersistence
{
    public static string? Serialize(ExactAcquisitionWorkbenchAuthority? authority) =>
        authority is null ? null : JsonConvert.SerializeObject(authority, Formatting.None);

    public static ExactAcquisitionWorkbenchAuthority? Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var authority = JsonConvert.DeserializeObject<ExactAcquisitionWorkbenchAuthority>(json);
            return authority is not null &&
                   string.Equals(authority.SchemaVersion, ExactAcquisitionWorkbenchAuthority.CurrentSchemaVersion, StringComparison.Ordinal)
                ? authority
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
