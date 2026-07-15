using System.Security.Cryptography;
using System.Text;

namespace MarketMafioso.Server.MarketAcquisition;

internal static class MarketAcquisitionRequestPolicy
{
    private static readonly IReadOnlyDictionary<string, string[]> SupportedSweepDataCenters =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["North America"] = ["Aether", "Primal", "Crystal", "Dynamis"],
            ["Europe"] = ["Chaos", "Light"],
            ["Japan"] = ["Elemental", "Gaia", "Mana", "Meteor"],
            ["Oceania"] = ["Materia"],
        };

    public static void ValidateCreateRequest(MarketAcquisitionCreateRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new ArgumentException("Schema version must be 1.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        ValidateOrigin(request.Origin, nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetCharacterName))
            throw new ArgumentException("Target character name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetWorld))
            throw new ArgumentException("Target world is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Region))
            throw new ArgumentException("Region is required.", nameof(request));
        var region = NormalizeSupportedRegion(request.Region, nameof(request));
        if (request.ItemId == 0)
            throw new ArgumentException("Item id is required.", nameof(request));
        if (request.MaxUnitPrice == 0)
            throw new ArgumentException("Max unit price is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.QuantityMode) ||
            string.IsNullOrWhiteSpace(request.HqPolicy) ||
            string.IsNullOrWhiteSpace(request.WorldMode))
            throw new ArgumentException("Quantity mode, HQ policy, and world mode are required.", nameof(request));
        if (request.QuantityMode is not ("TargetQuantity" or "AllBelowThreshold"))
            throw new ArgumentException("Quantity mode must be TargetQuantity or AllBelowThreshold.", nameof(request));
        if (request.QuantityMode == "TargetQuantity" && request.Quantity == 0)
            throw new ArgumentException("Target quantity is required.", nameof(request));
        ValidateSweepScope(region, request.WorldMode, request.SweepScope, request.SweepDataCenters, nameof(request));
        ValidateSelectedWorlds(request.WorldMode, request.SelectedWorlds, nameof(request));
    }

    public static void ValidateBatchCreateRequest(MarketAcquisitionBatchCreateRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new ArgumentException("Schema version must be 1.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        ValidateOrigin(request.Origin, nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetCharacterName))
            throw new ArgumentException("Target character name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetWorld))
            throw new ArgumentException("Target world is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Region))
            throw new ArgumentException("Region is required.", nameof(request));
        var region = NormalizeSupportedRegion(request.Region, nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorldMode))
            throw new ArgumentException("World mode is required.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("At least one acquisition line is required.", nameof(request));
        ValidateSweepScope(region, request.WorldMode, request.SweepScope, request.SweepDataCenters, nameof(request));
        ValidateSelectedWorlds(request.WorldMode, request.SelectedWorlds, nameof(request));

        foreach (var line in request.Lines)
            ValidateBatchLineCreateRequest(line);
    }

    public static string NormalizeOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin)
            ? MarketAcquisitionOrigins.DashboardCreated
            : origin.Trim();

    public static void ValidateBatchAppendLinesRequest(MarketAcquisitionBatchAppendLinesRequest request)
    {
        if (request.ExpectedRevision < 1)
            throw new ArgumentException("Expected revision must be one or greater.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("At least one acquisition line is required.", nameof(request));

        foreach (var line in request.Lines)
            ValidateBatchLineCreateRequest(line);
    }

    public static void ValidateBatchReplaceRequest(MarketAcquisitionBatchReplaceRequest request)
    {
        if (request.ExpectedRevision < 1)
            throw new ArgumentException("Expected revision must be one or greater.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Region))
            throw new ArgumentException("Region is required.", nameof(request));
        var region = NormalizeSupportedRegion(request.Region, nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorldMode))
            throw new ArgumentException("World mode is required.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("At least one acquisition line is required.", nameof(request));
        ValidateSweepScope(region, request.WorldMode, request.SweepScope, request.SweepDataCenters, nameof(request));
        ValidateSelectedWorlds(request.WorldMode, request.SelectedWorlds, nameof(request));

        foreach (var line in request.Lines)
            ValidateBatchLineCreateRequest(line);
    }

    public static void EnsureCanReplaceBatch(MarketAcquisitionRequestView current)
    {
        if (current.Status is MarketAcquisitionStatuses.Running
            or MarketAcquisitionStatuses.RecoveryRequired
            or MarketAcquisitionStatuses.Complete
            or MarketAcquisitionStatuses.Failed
            or MarketAcquisitionStatuses.Rejected
            or MarketAcquisitionStatuses.Expired
            or MarketAcquisitionStatuses.Cancelled)
        {
            throw new MarketAcquisitionInvalidTransitionException(current.Status, "editable request intent");
        }

        if (!string.IsNullOrWhiteSpace(current.LatestAttemptEventType) ||
            current.Lines.Any(line => line.PurchasedQuantity > 0 || line.SpentGil > 0))
        {
            throw new MarketAcquisitionInvalidTransitionException(current.Status, "editable request intent");
        }
    }

    public static string NormalizeSupportedRegion(string region, string argumentName)
    {
        var normalized = region.Trim();
        if (normalized.Equals("North-America", StringComparison.OrdinalIgnoreCase))
            normalized = "North America";

        return SupportedSweepDataCenters.ContainsKey(normalized)
            ? normalized
            : throw new ArgumentException($"{region} is not a supported market acquisition region.", argumentName);
    }

    public static IReadOnlyList<string> NormalizeSweepDataCenters(
        string region,
        IReadOnlyList<string> sweepDataCenters)
    {
        var normalizedRegion = NormalizeSupportedRegion(region, nameof(region));
        return sweepDataCenters
            .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
            .Select(dataCenter =>
            {
                var trimmed = dataCenter.Trim();
                var canonical = SupportedSweepDataCenters[normalizedRegion]
                    .FirstOrDefault(candidate => candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                return canonical ?? trimmed;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizeSelectedWorlds(IReadOnlyList<string> selectedWorlds) =>
        selectedWorlds
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Select(world => world.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void ValidateAttemptEventRequest(MarketAcquisitionAttemptEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken))
            throw new UnauthorizedAccessException("Claim token is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AttemptId))
            throw new ArgumentException("Attempt id is required.", nameof(request));
        if (request.EventSequence < 1)
            throw new ArgumentException("Attempt event sequence must be one or greater.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EventType))
            throw new ArgumentException("Attempt event type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Phase))
            throw new ArgumentException("Attempt phase is required.", nameof(request));
    }

    public static void ValidateLineProgressRequest(MarketAcquisitionLineProgressRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken))
            throw new UnauthorizedAccessException("Claim token is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AttemptId))
            throw new ArgumentException("Attempt id is required.", nameof(request));
        if (request.Sequence < 1)
            throw new ArgumentException("Line progress sequence must be one or greater.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Status))
            throw new ArgumentException("Line status is required.", nameof(request));
    }

    public static void ValidatePurchaseAuditRequest(MarketAcquisitionPurchaseAuditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken))
            throw new UnauthorizedAccessException("Claim token is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AttemptId))
            throw new ArgumentException("Attempt id is required.", nameof(request));
        if (request.Sequence < 1)
            throw new ArgumentException("Purchase audit sequence must be one or greater.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.LineId))
            throw new ArgumentException("Line id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorldName))
            throw new ArgumentException("World name is required.", nameof(request));
        if (request.ItemId == 0)
            throw new ArgumentException("Item id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ListingId))
            throw new ArgumentException("Listing id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RetainerName))
            throw new ArgumentException("Retainer name is required.", nameof(request));
        if (request.Quantity == 0)
            throw new ArgumentException("Purchase quantity is required.", nameof(request));
        if (request.UnitPrice == 0 || request.TotalGil == 0)
            throw new ArgumentException("Purchase gil values are required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Result))
            throw new ArgumentException("Purchase result is required.", nameof(request));
    }

    public static string CreateSecretToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    public static bool MatchesSecret(string supplied, string? stored)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(stored))
            return false;

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        return suppliedBytes.Length == storedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, storedBytes);
    }

    private static void ValidateOrigin(string? origin, string argumentName)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized is not (MarketAcquisitionOrigins.DashboardCreated or MarketAcquisitionOrigins.PluginBuilder or MarketAcquisitionOrigins.ClientQuickShop or MarketAcquisitionOrigins.CraftArchitect))
            throw new ArgumentException($"{normalized} is not a supported market acquisition origin.", argumentName);
    }

    private static void ValidateSweepScope(
        string region,
        string worldMode,
        string sweepScope,
        IReadOnlyList<string> sweepDataCenters,
        string argumentName)
    {
        if (!worldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(sweepScope))
            throw new ArgumentException("Sweep scope is required for all-world sweep.", argumentName);
        if (sweepScope is not ("Region" or "CurrentDataCenter" or "DataCenters"))
            throw new ArgumentException("Sweep scope must be Region, CurrentDataCenter, or DataCenters.", argumentName);
        if (sweepScope == "DataCenters" && sweepDataCenters.Count == 0)
            throw new ArgumentException("At least one data center is required for selected data-center sweep.", argumentName);
        if (sweepScope != "DataCenters")
            return;

        var supportedDataCenters = SupportedSweepDataCenters[region];
        var unsupported = sweepDataCenters
            .FirstOrDefault(dataCenter => !supportedDataCenters.Contains(dataCenter, StringComparer.OrdinalIgnoreCase));
        if (unsupported != null)
            throw new ArgumentException($"{unsupported} is not a {region} data center.", argumentName);
    }

    private static void ValidateSelectedWorlds(
        string worldMode,
        IReadOnlyList<string> selectedWorlds,
        string argumentName)
    {
        if (worldMode.Equals("Selected", StringComparison.OrdinalIgnoreCase) &&
            NormalizeSelectedWorlds(selectedWorlds).Count == 0)
        {
            throw new ArgumentException("At least one selected world is required for selected world mode.", argumentName);
        }
    }

    private static void ValidateBatchLineCreateRequest(MarketAcquisitionBatchLineCreateRequest line)
    {
        if (line.ItemId == 0)
            throw new ArgumentException("Item id is required.", nameof(line));
        if (line.MaxUnitPrice == 0)
            throw new ArgumentException("Max unit price is required.", nameof(line));
        if (string.IsNullOrWhiteSpace(line.QuantityMode) ||
            string.IsNullOrWhiteSpace(line.HqPolicy))
            throw new ArgumentException("Quantity mode and HQ policy are required.", nameof(line));
        if (line.QuantityMode is not ("TargetQuantity" or "AllBelowThreshold"))
            throw new ArgumentException("Quantity mode must be TargetQuantity or AllBelowThreshold.", nameof(line));
        if (line.QuantityMode == "TargetQuantity" && line.TargetQuantity == 0)
            throw new ArgumentException("Target quantity is required.", nameof(line));
    }
}
