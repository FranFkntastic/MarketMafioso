using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Franthropy.Dalamud.Travel;

namespace MarketMafioso.ProfileLogin;

public sealed class ProfileLoginRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Provider { get; set; } = "MarketMafioso";
    public string CharacterId { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public string HomeWorld { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset InitialSubmissionExpiresAtUtc { get; set; }
    public DateTimeOffset CompletionExpiresAtUtc { get; set; }
    public DateTimeOffset MinimumGameProcessStartUtc { get; set; }
    public int? ExpectedGameProcessId { get; set; }
    public bool AllowCharacterSwitch { get; set; }
}

public sealed class ProfileLoginBinding
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public string Provider { get; set; } = "MarketMafioso";
}

public sealed class ProfileLoginReceipt
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long Sequence { get; set; }
    public string Phase { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int? ProcessId { get; set; }
    public string? ObservedCharacterName { get; set; }
    public string? ObservedHomeWorld { get; set; }
    public string? SubmissionMode { get; set; }
}

public interface ICharacterLoginDriver
{
    LifestreamLoginSubmissionResult TryBegin(string characterName, string homeWorld, bool changeCharacter);
}

public sealed class DalamudCharacterLoginDriver(DalamudLifestreamLogin inner, Func<bool> providerLoaded) : ICharacterLoginDriver
{
    public LifestreamLoginSubmissionResult TryBegin(string characterName, string homeWorld, bool changeCharacter)
    {
        if (!LifestreamLoginRequest.TryCreate(characterName, homeWorld, out var request, out var error))
            return new(false, "InvalidRequest", error);
        // On a cold launch this plugin usually loads before Lifestream has registered its IPC. Nothing has been
        // submitted at that point, so it is a wait, not the ambiguous mid-submission failure IpcFailure stands for.
        if (!providerLoaded())
            return new(false, "NotReady", "Lifestream has not finished loading yet.");
        return changeCharacter ? inner.TryChangeCharacter(request!) : inner.TryBegin(request!);
    }

    /// <summary>Side-effect-free check that Lifestream's login IPC is registered in this process.</summary>
    public static bool IsLifestreamLoaded(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            pluginInterface.GetIpcSubscriber<bool>(DalamudLifestreamLogin.CanAutoLoginChannel).InvokeFunc();
            return true;
        }
        catch (IpcNotReadyError) { return false; }
        // Any other failure is left for the submission itself to classify.
        catch { return true; }
    }
}

public sealed class ProfileLoginCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICharacterLoginDriver driver;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<int> processId;
    private readonly Func<DateTimeOffset> processStartUtc;
    private readonly Func<(bool LoggedIn, string Name, string HomeWorld)> identity;
    private readonly Action<string, Exception?> diagnostic;
    private DateTimeOffset nextPollUtc;
    private static readonly TimeSpan RacyStampWindow = TimeSpan.FromSeconds(2);
    private readonly string bindingPath;
    private readonly string operationsRoot;
    private readonly Dictionary<string, CachedRead> reads = new(StringComparer.Ordinal);
    private long readGeneration;

    public ProfileLoginCoordinator(
        string pluginConfigDirectory,
        ICharacterLoginDriver driver,
        Func<(bool LoggedIn, string Name, string HomeWorld)> identity,
        Action<string, Exception?> diagnostic,
        Func<DateTimeOffset>? utcNow = null,
        Func<int>? processId = null,
        Func<DateTimeOffset>? processStartUtc = null)
    {
        var root = Path.Combine(pluginConfigDirectory, "profile-manager-login");
        bindingPath = Path.Combine(root, "provider-binding-v1.json");
        operationsRoot = Path.Combine(root, "operations");
        this.driver = driver;
        this.identity = identity;
        this.diagnostic = diagnostic;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.processId = processId ?? (() => Environment.ProcessId);
        this.processStartUtc = processStartUtc ?? DefaultProcessStartUtc;
    }

    private static DateTimeOffset DefaultProcessStartUtc() => System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public void Tick()
    {
        var now = utcNow();
        if (now < nextPollUtc) return;
        nextPollUtc = now.AddMilliseconds(500);
        try { TickCore(now); }
        catch (Exception ex) { diagnostic("Profile login coordination failed safely.", ex); }
    }

    internal void TickCore(DateTimeOffset now)
    {
        readGeneration++;
        try { TickFiles(now); }
        finally { SweepUnreadFiles(); }
    }

    private void TickFiles(DateTimeOffset now)
    {
        var binding = Read<ProfileLoginBinding>(bindingPath);
        if (binding is null || binding.SchemaVersion != 1 || binding.Provider != "MarketMafioso" || string.IsNullOrWhiteSpace(binding.ProfileId)) return;

        if (!Directory.Exists(operationsRoot)) return;
        // Newest eligible request wins; among equal timestamps the first enumerated wins, as a stable sort would pick.
        string? newestPath = null;
        ProfileLoginRequest? newest = null;
        foreach (var path in Directory.EnumerateFiles(operationsRoot, "request.json", SearchOption.AllDirectories))
        {
            var request = Read<ProfileLoginRequest>(path);
            if (request is null || request.SchemaVersion is not (1 or 2) || request.Provider != "MarketMafioso" ||
                request.ProfileId != binding.ProfileId || string.IsNullOrWhiteSpace(request.OperationId)) continue;
            if (newest is not null && request.RequestedAtUtc <= newest.RequestedAtUtc) continue;
            newest = request;
            newestPath = path;
        }
        if (newest is not null) Process(newestPath!, newest, now);
    }

    private void Process(string requestPath, ProfileLoginRequest request, DateTimeOffset now)
    {
        var receiptPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "receipt.json");
        var receipt = Read<ProfileLoginReceipt>(receiptPath);
        var pid = processId();
        if (File.Exists(receiptPath) && receipt is null)
        {
            Write(receiptPath, Next(null, request, "Blocked", "ReceiptCorrupt", "The existing receipt could not be validated; automatic submission is refused.", now, pid));
            return;
        }
        if (receipt is not null && (receipt.OperationId != request.OperationId || receipt.ProfileId != request.ProfileId))
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "ReceiptMismatch", "The existing receipt belongs to another operation; automatic submission is refused.", now, pid));
            return;
        }
        if (receipt?.Phase is "LoggedIn" or "Blocked" or "Failed" or "Expired") return;
        if (request.AllowCharacterSwitch && request.SchemaVersion != 2)
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "ProtocolUpgradeRequired", "Character switching requires direct-login protocol v2.", now, pid));
            return;
        }
        if (request.AllowCharacterSwitch && request.ExpectedGameProcessId is null)
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "ProcessBindingRequired", "Character switching requires an exact game-process binding.", now, pid));
            return;
        }
        if (request.ExpectedGameProcessId is int expectedPid && expectedPid != pid)
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "ExpectedProcessMismatch", "This request belongs to a different owned game process.", now, pid));
            return;
        }
        if (receipt?.ProcessId is int receiptPid && receiptPid != pid)
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "ProcessReplaced", "The game process changed after this operation began; automatic continuation is refused.", now, pid));
            return;
        }
        if (request.ExpectedGameProcessId is null && processStartUtc() < request.MinimumGameProcessStartUtc)
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "StaleProcess", "The request belongs to a newer game process.", now, pid));
            return;
        }

        var observed = identity();
        if (observed.LoggedIn)
        {
            var matches = observed.Name.Equals(request.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                          observed.HomeWorld.Equals(request.HomeWorld, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                Write(receiptPath, Next(receipt, request, "LoggedIn", "ExactIdentity", "The requested character is logged in.",
                    now, pid, observed.Name, observed.HomeWorld, receipt?.SubmissionMode));
                return;
            }
            if (receipt?.Phase == "Submitted" && request.AllowCharacterSwitch && receipt.SubmissionMode == "CharacterSwitch")
            {
                if (now > request.CompletionExpiresAtUtc)
                    Write(receiptPath, Next(receipt, request, "Failed", "CompletionTimeout", "Character switching did not reach the exact character before its completion deadline.", now, pid,
                        observed.Name, observed.HomeWorld, receipt.SubmissionMode));
                return;
            }
            if (!request.AllowCharacterSwitch)
            {
                Write(receiptPath, Next(receipt, request, "Blocked", "IdentityMismatch", "A different character is logged in; this request did not authorize switching.",
                    now, pid, observed.Name, observed.HomeWorld, receipt?.SubmissionMode));
                return;
            }
        }

        if (receipt?.Phase == "Submitting")
        {
            Write(receiptPath, Next(receipt, request, "Blocked", "AmbiguousSubmission", "MMF restarted while submission outcome was ambiguous; automatic replay is refused.", now, pid));
            return;
        }
        if (receipt?.Phase == "Submitted")
        {
            if (now > request.CompletionExpiresAtUtc)
                Write(receiptPath, Next(receipt, request, "Failed", "CompletionTimeout", "Login did not reach the exact character before its completion deadline.", now, pid, submissionMode: receipt.SubmissionMode));
            return;
        }
        if (now > request.InitialSubmissionExpiresAtUtc)
        {
            Write(receiptPath, Next(receipt, request, "Expired", "SubmissionExpired", "The login driver never became ready before the submission deadline.", now, pid));
            return;
        }

        receipt = Next(receipt, request, "Submitting", "Dispatching", "Submitting the exact character to the configured login driver.", now, pid);
        Write(receiptPath, receipt);
        var changeCharacter = observed.LoggedIn && request.AllowCharacterSwitch;
        var result = driver.TryBegin(request.CharacterName, request.HomeWorld, changeCharacter);
        if (result.Success)
        {
            Write(receiptPath, Next(receipt, request, "Submitted", result.Code, result.Message, now, pid, submissionMode: result.SubmissionMode));
            return;
        }
        var retryable = result.Code == "NotReady";
        var phase = result.Code == "IpcFailure" ? "Blocked" : retryable ? "WaitingForDriver" : "Failed";
        Write(receiptPath, Next(receipt, request, phase, result.Code, result.Message, now, pid, submissionMode: result.SubmissionMode));
    }

    private static ProfileLoginReceipt Next(ProfileLoginReceipt? previous, ProfileLoginRequest request, string phase, string code,
        string message, DateTimeOffset now, int pid, string? name = null, string? world = null, string? submissionMode = null) => new()
    {
        SchemaVersion = request.SchemaVersion,
        OperationId = request.OperationId,
        ProfileId = request.ProfileId,
        Sequence = (previous?.Sequence ?? 0) + 1,
        Phase = phase,
        Code = code,
        Message = message,
        UpdatedAtUtc = now,
        ProcessId = pid,
        ObservedCharacterName = name,
        ObservedHomeWorld = world,
        SubmissionMode = submissionMode,
    };

    /// <summary>
    /// Parses a coordination file, reusing the previous parse while its last-write time and length are unchanged.
    /// The binding and every historical request are polled twice a second but change only when the profile manager
    /// (or this coordinator) rewrites them, so the steady state is a metadata check instead of a read and a parse.
    /// A file written within <see cref="RacyStampWindow"/> of now is never cached, because a second write landing in
    /// the same timestamp tick with an equal length would be invisible to the stamp check. Parsed objects are shared
    /// across polls and must not be mutated.
    /// </summary>
    private T? Read<T>(string path) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reads.Remove(path);
                return null;
            }
            var lastWriteUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            if (reads.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc && cached.Length == length)
            {
                cached.Generation = readGeneration;
                return cached.Value as T;
            }

            var text = File.ReadAllText(path);
            T? value;
            try { value = JsonSerializer.Deserialize<T>(text, JsonOptions); }
            catch (JsonException) { value = null; }
            if (DateTime.UtcNow - lastWriteUtc > RacyStampWindow && text.Length > 0)
                reads[path] = new CachedRead(lastWriteUtc, length, value) { Generation = readGeneration };
            else
                reads.Remove(path);
            return value;
        }
        catch
        {
            reads.Remove(path);
            return null;
        }
    }

    /// <summary>Drops cached parses for files this poll did not read, so retired operations do not accumulate.</summary>
    private void SweepUnreadFiles()
    {
        foreach (var (path, cached) in reads)
        {
            if (cached.Generation != readGeneration) reads.Remove(path);
        }
    }

    private sealed class CachedRead(DateTime lastWriteUtc, long length, object? value)
    {
        public DateTime LastWriteUtc { get; } = lastWriteUtc;
        public long Length { get; } = length;
        public object? Value { get; } = value;
        public long Generation { get; set; }
    }

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
}
