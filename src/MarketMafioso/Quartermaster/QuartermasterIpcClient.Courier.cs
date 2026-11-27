using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace MarketMafioso.Quartermaster;

/// <summary>Quartermaster's cross-region Courier engine as seen by a consumer.</summary>
public interface IQuartermasterCourierClient
{
    /// <summary>True only when Quartermaster advertises crossRegionCourier.v1 and exposes its channels. Never throws.</summary>
    bool HasCrossRegionCourier { get; }
    bool TryGetCourierStatus(out QuartermasterCourierStatus? status, out string error);
    bool TrySubmitCourierTransfer(QuartermasterCourierTransferRequest request, out QuartermasterCourierAcknowledgement? acknowledgement, out string error);
    bool TryStopCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error);
    bool TryResumeCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error);
    /// <summary>Pairs <paramref name="owner"/> on the configured carrier. Blocks for the carrier round trip; call off the framework thread.</summary>
    bool TryPairCourierOwner(QuartermasterCourierCharacter owner, out QuartermasterCourierAcknowledgement? acknowledgement, out string error);
}

public sealed partial class QuartermasterIpcClient : IQuartermasterCourierClient
{
    public const string CrossRegionCourierCapability = "crossRegionCourier.v1";
    public const string GetCourierStatusChannel = "Quartermaster.v1.GetCourierStatus";
    public const string SubmitCourierTransferChannel = "Quartermaster.v1.SubmitCourierTransfer";
    public const string StopCourierRunChannel = "Quartermaster.v1.StopCourierRun";
    public const string ResumeCourierRunChannel = "Quartermaster.v1.ResumeCourierRun";
    public const string PairCourierOwnerChannel = "Quartermaster.v1.PairCourierOwner";
    public const string CourierStatusSchema = "gooseworks-quartermaster-courier-status/v1";
    public const string CourierTransferSchema = "gooseworks-quartermaster-courier-transfer/v1";
    public const string CourierTransferAckSchema = "gooseworks-quartermaster-courier-transfer-ack/v1";

    public bool HasCrossRegionCourier
    {
        get
        {
            try
            {
                if (!adapter.HasCourierStatus || !adapter.HasSubmitCourierTransfer)
                    return false;
                return TryGetCapabilities(out var capabilities, out _) &&
                       capabilities!.Capabilities.Contains(CrossRegionCourierCapability, StringComparer.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryGetCourierStatus(out QuartermasterCourierStatus? status, out string error)
    {
        status = null;
        if (!EnsureUsable(out error))
            return false;
        bool available;
        try { available = adapter.HasCourierStatus; }
        catch (Exception ex) { error = $"Quartermaster courier availability check failed: {ex.Message}"; return false; }
        if (!available)
        {
            error = "Quartermaster is not loaded or does not expose the cross-region courier.";
            return false;
        }

        string json;
        try { json = adapter.GetCourierStatus(); }
        catch (Exception ex) { error = $"Quartermaster courier status call failed: {ex.Message}"; return false; }
        return TryParseCourierStatus(json, out status, out error);
    }

    public bool TrySubmitCourierTransfer(
        QuartermasterCourierTransferRequest request,
        out QuartermasterCourierAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidateCourierRequest(request, out error))
            return false;
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (!capabilities!.Capabilities.Contains(CrossRegionCourierCapability, StringComparer.Ordinal))
        {
            error = "Quartermaster does not advertise the cross-region courier; nothing was submitted.";
            return false;
        }
        bool available;
        try { available = adapter.HasSubmitCourierTransfer; }
        catch (Exception ex) { error = $"Quartermaster courier availability check failed: {ex.Message}"; return false; }
        if (!available)
        {
            error = "Quartermaster does not expose courier transfer submission; nothing was submitted.";
            return false;
        }

        var json = JsonSerializer.Serialize(ToCourierWire(request, capabilities.ProviderInstanceId), JsonOptions);
        string ackJson;
        try { ackJson = adapter.SubmitCourierTransfer(json); }
        catch (Exception ex)
        {
            error = $"Quartermaster courier submission failed; acceptance is unknown: {ex.Message}";
            return false;
        }
        if (!TryParseCourierAcknowledgement(ackJson, out acknowledgement, out error))
        {
            error = $"Quartermaster returned an invalid courier acknowledgement; acceptance is unknown. {error}";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(acknowledgement!.RequestId) &&
            !string.Equals(acknowledgement.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            error = "Quartermaster acknowledged a different courier request; acceptance is unknown.";
            acknowledgement = null;
            return false;
        }
        return true;
    }

    public bool TryStopCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error) =>
        TryCourierRunCommand(runId, stop: true, out acknowledgement, out error);

    public bool TryResumeCourierRun(string runId, out QuartermasterCourierAcknowledgement? acknowledgement, out string error) =>
        TryCourierRunCommand(runId, stop: false, out acknowledgement, out error);

    public bool TryPairCourierOwner(QuartermasterCourierCharacter owner, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.LocalContentId == 0 || owner.HomeWorldId == 0)
        {
            error = "A known character is required to pair it on the carrier.";
            return false;
        }
        if (!EnsureUsable(out error))
            return false;
        bool available;
        try { available = adapter.HasPairCourierOwner; }
        catch (Exception ex) { error = $"Quartermaster courier availability check failed: {ex.Message}"; return false; }
        if (!available)
        {
            error = "Quartermaster does not expose courier owner pairing; update Quartermaster.";
            return false;
        }
        string json;
        var payload = JsonSerializer.Serialize(ToCourierWire(owner), JsonOptions);
        try { json = adapter.PairCourierOwner(payload); }
        catch (Exception ex)
        {
            error = $"Quartermaster courier pairing failed; outcome is unknown: {ex.Message}";
            return false;
        }
        return TryParseCourierAcknowledgement(json, out acknowledgement, out error);
    }

    private bool TryCourierRunCommand(string runId, bool stop, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        var verb = stop ? "stop" : "resume";
        if (string.IsNullOrWhiteSpace(runId))
        {
            error = $"A courier run id is required to {verb} it.";
            return false;
        }
        if (!EnsureUsable(out error))
            return false;
        bool available;
        try { available = stop ? adapter.HasStopCourierRun : adapter.HasResumeCourierRun; }
        catch (Exception ex) { error = $"Quartermaster courier availability check failed: {ex.Message}"; return false; }
        if (!available)
        {
            error = $"Quartermaster does not expose courier {verb}.";
            return false;
        }
        string json;
        var payload = JsonSerializer.Serialize(new { runId }, JsonOptions);
        try { json = stop ? adapter.StopCourierRun(payload) : adapter.ResumeCourierRun(payload); }
        catch (Exception ex)
        {
            error = $"Quartermaster courier {verb} failed; outcome is unknown: {ex.Message}";
            return false;
        }
        return TryParseCourierAcknowledgement(json, out acknowledgement, out error);
    }

    private static bool ValidateCourierRequest(QuartermasterCourierTransferRequest request, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(request.RequestId))
            error = "Courier request id is required.";
        else if (request.Source.LocalContentId == 0 || request.Source.HomeWorldId == 0 ||
                 request.Destination.LocalContentId == 0 || request.Destination.HomeWorldId == 0)
            error = "Courier source and destination must be known characters.";
        else if (request.Source.LocalContentId == request.Destination.LocalContentId)
            error = "Courier source and destination must differ.";
        else if (request.GilAmount < 0)
            error = "Courier gil amount cannot be negative.";
        else if (request.Items.Any(item => item.ItemId == 0 || item.Quantity <= 0))
            error = "Courier items need an item id and a positive quantity.";
        else if (request.GilAmount == 0 && request.Items.IsDefaultOrEmpty)
            error = "Courier transfer must move gil or items.";
        return error.Length == 0;
    }

    private static QuartermasterCourierTransferRequestWire ToCourierWire(QuartermasterCourierTransferRequest request, string providerInstanceId) => new()
    {
        Schema = CourierTransferSchema,
        ProviderInstanceId = providerInstanceId,
        RequestId = request.RequestId,
        Source = ToCourierWire(request.Source),
        Destination = ToCourierWire(request.Destination),
        GilAmount = request.GilAmount,
        Items = (request.Items.IsDefault ? [] : request.Items).Select(item => new QuartermasterCourierItemWire
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            IsHq = item.IsHq,
            Quantity = item.Quantity,
        }).ToList(),
        ReturnOwner = request.ReturnOwner,
        ReturnDestinationHome = request.ReturnDestinationHome,
        ReturnCarrier = request.ReturnCarrier,
        DepositToRetainers = request.DepositToRetainers,
    };

    private static QuartermasterCourierCharacterWire ToCourierWire(QuartermasterCourierCharacter character) => new()
    {
        LocalContentId = character.LocalContentId,
        HomeWorldId = character.HomeWorldId,
        CharacterName = character.CharacterName,
        HomeWorldName = character.HomeWorldName,
    };

    private static bool TryParseCourierStatus(string json, out QuartermasterCourierStatus? status, out string error)
    {
        status = null;
        if (!TryDeserialize(json, out QuartermasterCourierStatusWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, CourierStatusSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster courier status schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(wire.Error))
        {
            error = $"Quartermaster courier status error: {wire.Error}";
            return false;
        }

        var loggedIn = wire.LoggedIn is { LocalContentId: > 0 } li
            ? new QuartermasterCourierLoggedIn(li.LocalContentId, li.HomeWorldId, li.CharacterName ?? string.Empty,
                li.HomeWorldName ?? string.Empty, RegionText(li.RegionId), li.RegionName, li.CarriedGil, li.FreeBagSlots,
                li.HasMeetingBell, li.MeetingBellLabel, li.IsPairedWithCarrier, li.RetainerFreeSlots)
            : null;
        var carrier = wire.Carrier is { } c
            ? new QuartermasterCourierCarrier(c.Configured, c.Online, c.LocalContentId ?? 0, c.HomeWorldId ?? 0, c.CharacterName,
                c.HomeWorldName, RegionText(c.RegionId), c.RegionName, c.CarriedGil ?? 0, c.FreeBagSlots ?? 0, c.CarrierOptIn)
            : null;
        var bridge = wire.Bridge is { } b ? new QuartermasterCourierBridge(b.WorldId, b.WorldName, b.TerritoryId, b.Label) : null;
        var roster = (wire.Roster ?? [])
            .Where(entry => entry.LocalContentId > 0)
            .Select(entry => new QuartermasterCourierRosterEntry(entry.LocalContentId, entry.HomeWorldId,
                entry.CharacterName ?? string.Empty, entry.HomeWorldName ?? string.Empty, RegionText(entry.RegionId),
                entry.RegionName, entry.HasMeetingBell, entry.MeetingBellLabel, entry.IsCarrier, entry.IsPairedWithCarrier,
                entry.FreeBagSlots, entry.RetainerFreeSlots))
            .ToImmutableArray();
        var run = wire.Run is { } r && !string.IsNullOrWhiteSpace(r.State)
            ? new QuartermasterCourierRun(r.RunId, r.RequestId, r.State!, r.OwnerPhase, r.CarrierPhase, r.Trip, r.Trips,
                r.Code, r.Detail, r.Active, r.CanResume, r.CanClear, r.CustodyUnits, r.CustodyGil)
            {
                Deposit = r.Deposit is { } deposit ? new QuartermasterCourierDeposit(deposit.UnstowedUnits, deposit.Code) : null,
            }
            : null;
        status = new QuartermasterCourierStatus(wire.ProviderInstanceId ?? string.Empty, loggedIn, carrier, bridge, roster, run)
        {
            Features = (wire.Features ?? []).Where(feature => !string.IsNullOrWhiteSpace(feature)).ToImmutableArray(),
        };
        return true;
    }

    private static bool TryParseCourierAcknowledgement(string json, out QuartermasterCourierAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        if (!TryDeserialize(json, out QuartermasterCourierAcknowledgementWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, CourierTransferAckSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster courier acknowledgement schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(wire.Status))
        {
            error = "Quartermaster courier acknowledgement omitted its status.";
            return false;
        }
        acknowledgement = new QuartermasterCourierAcknowledgement(wire.ProviderInstanceId, wire.RequestId, wire.RunId,
            wire.Status.Trim().ToLowerInvariant(), wire.ErrorCode, wire.Message);
        return true;
    }

    private static string? RegionText(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };
}
