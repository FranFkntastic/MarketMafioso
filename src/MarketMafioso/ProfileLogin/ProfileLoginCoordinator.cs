using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class DalamudCharacterLoginDriver(DalamudLifestreamLogin inner) : ICharacterLoginDriver
{
    public LifestreamLoginSubmissionResult TryBegin(string characterName, string homeWorld, bool changeCharacter) =>
        LifestreamLoginRequest.TryCreate(characterName, homeWorld, out var request, out var error)
            ? changeCharacter ? inner.TryChangeCharacter(request!) : inner.TryBegin(request!)
            : new(false, "InvalidRequest", error);
}

public sealed class ProfileLoginCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string root;
    private readonly ICharacterLoginDriver driver;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<int> processId;
    private readonly Func<DateTimeOffset> processStartUtc;
    private readonly Func<(bool LoggedIn, string Name, string HomeWorld)> identity;
    private readonly Action<string, Exception?> diagnostic;
    private DateTimeOffset nextPollUtc;

    public ProfileLoginCoordinator(
        string pluginConfigDirectory,
        ICharacterLoginDriver driver,
        Func<(bool LoggedIn, string Name, string HomeWorld)> identity,
        Action<string, Exception?> diagnostic,
        Func<DateTimeOffset>? utcNow = null,
        Func<int>? processId = null,
        Func<DateTimeOffset>? processStartUtc = null)
    {
        root = Path.Combine(pluginConfigDirectory, "profile-manager-login");
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
        var binding = Read<ProfileLoginBinding>(Path.Combine(root, "provider-binding-v1.json"));
        if (binding is null || binding.SchemaVersion != 1 || binding.Provider != "MarketMafioso" || string.IsNullOrWhiteSpace(binding.ProfileId)) return;

        var operationsRoot = Path.Combine(root, "operations");
        if (!Directory.Exists(operationsRoot)) return;
        var requests = Directory.EnumerateFiles(operationsRoot, "request.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Request: Read<ProfileLoginRequest>(path)))
            .Where(item => item.Request is not null)
            .OrderByDescending(item => item.Request!.RequestedAtUtc);
        foreach (var item in requests)
        {
            var request = item.Request!;
            if (request.SchemaVersion is not (1 or 2) || request.Provider != "MarketMafioso" ||
                request.ProfileId != binding.ProfileId || string.IsNullOrWhiteSpace(request.OperationId)) continue;
            Process(item.Path, request, now);
            return;
        }
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

    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default; }
        catch { return default; }
    }

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
}
