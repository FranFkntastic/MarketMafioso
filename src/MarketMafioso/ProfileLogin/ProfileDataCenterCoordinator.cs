using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Franthropy.Dalamud.Travel;

namespace MarketMafioso.ProfileLogin;

public sealed class DataCenterConnectionRequest
{
    public int SchemaVersion { get; set; }
    public string OperationId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string DataCenter { get; set; } = "";
    public DataCenterServiceAccountChoice? ServiceAccount { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset DispatchExpiresAtUtc { get; set; }
    public DateTimeOffset CompletionExpiresAtUtc { get; set; }
    public DateTimeOffset MinimumProcessStartUtc { get; set; }
}
public sealed record DataCenterProcessBinding(int SchemaVersion, string ProfileId, string OperationId,
    int ExpectedProcessId, DateTimeOffset ExpectedProcessStartUtc, DateTimeOffset BoundAtUtc,
    DateTimeOffset DispatchExpiresAtUtc, DateTimeOffset CompletionExpiresAtUtc);
public sealed class DataCenterConnectionReceipt
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string RequestSha256 { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartUtc { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Phase { get; set; } = "WaitingForTitle";
    public string Code { get; set; } = "";
    public string? CanonicalDataCenter { get; set; }
    public bool? IsLoggedIn { get; set; }
    public bool? PlayerAvailable { get; set; }
    public string? DispatchIntent { get; set; }
    public HashSet<string> Sent { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Plugin-owned, bounded lobby coordination, independent of character login.</summary>
public sealed class ProfileDataCenterCoordinator(string configDirectory, DalamudDataCenterConnection driver)
{
    private readonly string root = Path.Combine(configDirectory, "profile-manager-data-center");
    private readonly DateTimeOffset processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private DateTimeOffset nextPoll;
    private readonly HashSet<string> retired = new(StringComparer.Ordinal);
    private readonly HashSet<string> deferred = new(StringComparer.Ordinal);
    private DateTimeOffset nextDiscovery;
    private string[] paths = [];
    public bool OwnsLobby { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static bool Terminal(string phase) => phase is "CharacterSelectionReady" or "Blocked" or "Failed";
    private static T? Read<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    private static byte[] Bytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }
    private static void Write<T>(string path, T value) => ProfileLoginCoordinator.Write(path, value);

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now.AddMilliseconds(500);
        try { TickCore(now); }
        catch (Exception ex)
        {
            OwnsLobby = false;
            System.Diagnostics.Trace.TraceError("Data center coordination stopped: {0}", ex.Message);
        }
        if (!OwnsLobby) nextPoll = now.AddSeconds(5);
    }

    private void TickCore(DateTimeOffset now)
    {
        OwnsLobby = false;
        var operations = Path.Combine(root, "operations");
        if (!Directory.Exists(operations)) return;
        // Discovery is deliberately low rate; only known active requests are read at UI cadence.
        if (now >= nextDiscovery)
        {
            paths = Directory.GetDirectories(operations)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
            retired.Clear();
            deferred.Clear();
            foreach (var path in paths)
            {
                if (Read<DataCenterConnectionRequest>(Path.Combine(path, "request.json")) is not null) continue;
                deferred.Add(path);
                try { RetirePartial(path, now); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            nextDiscovery = now.AddSeconds(5);
        }
        var active = paths.Where(path => !retired.Contains(path) && !deferred.Contains(path))
            .Select(path => (Path: path, Request: Read<DataCenterConnectionRequest>(Path.Combine(path, "request.json"))))
            .Where(item => item.Request is not null)
            .ToArray();
        if (active.Length == 0) return;
        foreach (var item in active)
        {
          try
          {
            var requestBytes = Bytes(Path.Combine(item.Path, "request.json"));
            var request = JsonSerializer.Deserialize<DataCenterConnectionRequest>(requestBytes, Json)!;
            var receiptPath = Path.Combine(item.Path, "receipt.json");
            var previous = Read<DataCenterConnectionReceipt>(receiptPath);
            var hash = Convert.ToHexString(SHA256.HashData(requestBytes));
            // Never replace an unreadable receipt: it may contain a persisted dispatch intent.
            if (previous is null && File.Exists(receiptPath)) { deferred.Add(item.Path); continue; }
            var receipt = previous ?? new() { ProfileId = request.ProfileId, OperationId = request.OperationId,
                RequestSha256 = hash, ProcessId = Environment.ProcessId, ProcessStartUtc = processStart };
            void Save(string phase, string code)
            {
                var before = File.Exists(receiptPath) ? Read<DataCenterConnectionReceipt>(receiptPath) : null;
                receipt.Phase = phase; receipt.Code = code; receipt.Sequence++; receipt.UpdatedAtUtc = now;
                if (before is not null && before.Phase == phase && before.Code == code &&
                    before.DispatchIntent == receipt.DispatchIntent && before.Sent.SetEquals(receipt.Sent) &&
                    before.CanonicalDataCenter == receipt.CanonicalDataCenter && before.IsLoggedIn == receipt.IsLoggedIn &&
                    before.PlayerAvailable == receipt.PlayerAvailable) return;
                Write(receiptPath, receipt);
                if (Terminal(phase)) OwnsLobby = false;
            }
            if (request.SchemaVersion != 1 || !Guid.TryParseExact(request.OperationId, "N", out _) ||
                Path.GetFileName(item.Path) != request.OperationId || !Guid.TryParseExact(request.ProfileId, "N", out _)) { deferred.Add(item.Path); continue; }
            if (previous is not null && (previous.RequestSha256 != hash || previous.ProfileId != request.ProfileId || previous.OperationId != request.OperationId)) { deferred.Add(item.Path); continue; }
            if (Terminal(receipt.Phase)) { Retire(item.Path); continue; }
            var cancellation = Read<JsonElement>(Path.Combine(item.Path, "cancelled.json"));
            if (cancellation.ValueKind == JsonValueKind.Object && cancellation.TryGetProperty("OperationId", out var cancelledId) &&
                cancelledId.GetString() == request.OperationId &&
                (!cancellation.TryGetProperty("ProfileId", out var cancelledProfile) || cancelledProfile.GetString() == request.ProfileId) &&
                (!cancellation.TryGetProperty("RequestSha256", out var cancelledHash) || cancelledHash.GetString() == hash))
            { Save("Blocked", "OwnerCancelled"); Retire(item.Path); continue; }
            var binding = Read<DataCenterProcessBinding>(Path.Combine(item.Path, "process-binding.json"));
            if (binding is null)
            {
                if (now > request.CompletionExpiresAtUtc) { Save("Failed", "StartupDeadline"); Retire(item.Path); }
                deferred.Add(item.Path);
                continue; // An unbound request never owns the lobby.
            }
            if (now > binding.CompletionExpiresAtUtc) { Save("Failed", "CompletionDeadline"); Retire(item.Path); continue; }
            if (active.Any(candidate => candidate.Path != item.Path && !retired.Contains(candidate.Path) &&
                candidate.Request is { SchemaVersion: 1 } otherRequest && otherRequest.ProfileId == request.ProfileId &&
                otherRequest.OperationId == Path.GetFileName(candidate.Path) && Guid.TryParseExact(otherRequest.OperationId, "N", out _) &&
                !Terminal(Read<DataCenterConnectionReceipt>(Path.Combine(candidate.Path, "receipt.json"))?.Phase ?? "") &&
                !Cancelled(candidate.Path, otherRequest) &&
                Read<DataCenterProcessBinding>(Path.Combine(candidate.Path, "process-binding.json")) is { SchemaVersion: 1 } other &&
                other.ProfileId == request.ProfileId && other.OperationId == otherRequest.OperationId &&
                other.ExpectedProcessId == Environment.ProcessId && other.ExpectedProcessStartUtc == processStart &&
                other.CompletionExpiresAtUtc >= now))
            { Save("Blocked", "ConcurrentOperations"); continue; }
            var loginOperations = Path.Combine(configDirectory, "profile-manager-login", "operations");
            if (Directory.Exists(loginOperations))
            {
                var newest = Directory.EnumerateDirectories(loginOperations)
                    .Select(path => (Path: path, Request: Read<ProfileLoginRequest>(Path.Combine(path, "request.json"))))
                    .Where(x => x.Request is { SchemaVersion: 1 or 2, Provider: "MarketMafioso" } login &&
                        login.ProfileId == request.ProfileId && !string.IsNullOrWhiteSpace(login.OperationId))
                    .OrderByDescending(x => x.Request!.RequestedAtUtc).FirstOrDefault();
                if (newest.Request is { } login && login.CompletionExpiresAtUtc > now &&
                    Read<ProfileLoginReceipt>(Path.Combine(newest.Path, "receipt.json"))?.Phase is not ("LoggedIn" or "Blocked" or "Failed" or "Expired"))
                { Save("Blocked", "CharacterLoginOperationActive"); continue; }
            }
            if (request.SchemaVersion != 1 || !Guid.TryParseExact(request.OperationId, "N", out _) ||
                Path.GetFileName(item.Path) != request.OperationId || !Guid.TryParseExact(request.ProfileId, "N", out _) ||
                string.IsNullOrWhiteSpace(request.DataCenter) || request.DataCenter.Length > 64 || request.DataCenter.Any(char.IsControl) ||
                request.RequestedAtUtc > now || request.DispatchExpiresAtUtc > request.CompletionExpiresAtUtc ||
                request.CompletionExpiresAtUtc <= request.RequestedAtUtc ||
                request.CompletionExpiresAtUtc - request.RequestedAtUtc > TimeSpan.FromHours(2))
            { Save("Blocked", "InvalidRequest"); return; }
            if (receipt.RequestSha256 != hash || receipt.ProfileId != request.ProfileId || receipt.OperationId != request.OperationId ||
                receipt.ProcessId != Environment.ProcessId || receipt.ProcessStartUtc != processStart)
            { Save("Blocked", "IdentityChanged"); return; }
            if (receipt.DispatchIntent is not null) { Save("Blocked", "AmbiguousDispatch"); return; }
            if (binding.SchemaVersion != 1 || binding.ProfileId != request.ProfileId || binding.OperationId != request.OperationId ||
                binding.ExpectedProcessId != Environment.ProcessId || binding.ExpectedProcessStartUtc != processStart ||
                processStart < request.MinimumProcessStartUtc || binding.BoundAtUtc < request.RequestedAtUtc ||
                binding.BoundAtUtc > request.CompletionExpiresAtUtc || binding.BoundAtUtc > now ||
                binding.DispatchExpiresAtUtc != binding.BoundAtUtc.AddMinutes(5) ||
                binding.CompletionExpiresAtUtc != binding.BoundAtUtc.AddMinutes(10))
            { Save("Blocked", "ProcessBindingMismatch"); return; }
            OwnsLobby = true;
            var observed = driver.Observe(request.DataCenter, receipt.Sent, request.ServiceAccount);
            receipt.CanonicalDataCenter = observed.CanonicalDataCenter;
            if (observed.Phase == "CharacterSelectionReady") { receipt.IsLoggedIn = false; receipt.PlayerAvailable = false; }
            Save(observed.Phase, observed.Code);
            if (Terminal(observed.Phase) || observed.NextInput is null) return;
            if (now > binding.DispatchExpiresAtUtc) { Save("Failed", "DispatchDeadline"); return; }
            receipt.DispatchIntent = observed.NextInput;
            Save(observed.Phase, "DispatchIntentPersisted");
            try
            {
                driver.Dispatch(request.DataCenter, receipt.Sent, request.ServiceAccount, observed.NextInput);
                receipt.Sent.Add(observed.NextInput);
                receipt.DispatchIntent = null;
                Save(observed.Phase, "InputSentAwaitingObservation");
            }
            catch (DataCenterPreInputRefusal) { receipt.DispatchIntent = null; Save(observed.Phase, "PreInputStateChanged"); }
            catch { Save("Blocked", "AmbiguousDispatch"); }
            return;
          }
          catch (Exception ex)
          {
              // A failed intent write exits before Dispatch. A failed acknowledgement leaves the durable
              // intent intact, so the next successful read blocks this operation rather than replaying it.
              OwnsLobby = false;
              deferred.Add(item.Path);
              Trace.TraceError("Data center operation {0}: {1}", Path.GetFileName(item.Path), ex.Message);
          }
        }
    }

    private void Retire(string path)
    {
        var archive = Path.Combine(root, "history");
        Directory.CreateDirectory(archive);
        Directory.Move(path, Path.Combine(archive, Path.GetFileName(path)));
        retired.Add(path);
        foreach (var old in Directory.GetDirectories(archive).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(64))
        {
            if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) continue;
            var request = Read<DataCenterConnectionRequest>(Path.Combine(old, "request.json"));
            var receipt = Read<DataCenterConnectionReceipt>(Path.Combine(old, "receipt.json"));
            if (request is not null && receipt is not null && receipt.DispatchIntent is null && Terminal(receipt.Phase) &&
                request.OperationId == Path.GetFileName(old) && receipt.OperationId == request.OperationId &&
                receipt.ProfileId == request.ProfileId && receipt.RequestSha256 == Convert.ToHexString(SHA256.HashData(Bytes(Path.Combine(old, "request.json")))))
                Directory.Delete(old, true);
        }
    }

    private static bool Cancelled(string path, DataCenterConnectionRequest request)
    {
        var value = Read<JsonElement>(Path.Combine(path, "cancelled.json"));
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("OperationId", out var id) ||
            id.GetString() != request.OperationId ||
            (value.TryGetProperty("ProfileId", out var profile) && profile.GetString() != request.ProfileId)) return false;
        if (!value.TryGetProperty("RequestSha256", out var sha)) return true;
        try { return sha.GetString() == Convert.ToHexString(SHA256.HashData(Bytes(Path.Combine(path, "request.json")))); }
        catch (IOException) { return false; }
    }

    private void RetirePartial(string path, DateTimeOffset now)
    {
        if (File.Exists(Path.Combine(path, "process-binding.json")) || File.Exists(Path.Combine(path, "receipt.json"))) return;
        var candidates = Directory.GetFiles(path, "request.json.*.tmp")
            .Select(Read<DataCenterConnectionRequest>).Where(x => x is not null).ToArray();
        if (candidates.Length != 1 || candidates[0] is not { SchemaVersion: 1 } request ||
            !Guid.TryParseExact(request.ProfileId, "N", out _) || !Guid.TryParseExact(request.OperationId, "N", out _) ||
            request.OperationId != Path.GetFileName(path) || request.CompletionExpiresAtUtc >= now) return;
        var archive = Path.Combine(root, "incomplete-history");
        Directory.CreateDirectory(archive);
        Directory.Move(path, Path.Combine(archive, request.OperationId));
        foreach (var old in Directory.GetDirectories(archive).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(64))
        {
            if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(Path.Combine(old, "process-binding.json")) || File.Exists(Path.Combine(old, "receipt.json"))) continue;
            var retained = Directory.GetFiles(old, "request.json.*.tmp").Select(Read<DataCenterConnectionRequest>).Where(x => x is not null).ToArray();
            if (retained.Length == 1 && retained[0] is { SchemaVersion: 1 } evidence &&
                evidence.ProfileId == request.ProfileId && evidence.OperationId == Path.GetFileName(old) &&
                Guid.TryParseExact(evidence.OperationId, "N", out _) && evidence.CompletionExpiresAtUtc < now)
                Directory.Delete(old, true);
        }
    }
}
