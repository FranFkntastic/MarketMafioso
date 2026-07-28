using System;
using System.Diagnostics;
using System.IO;

namespace MarketMafioso.Automation.Runtime;

internal readonly record struct GamePatchCompatibility(
    string ContractId,
    string ApprovedGameVersion,
    string CurrentGameVersion,
    bool IsApproved)
{
    public const string FailureCode = "UnsupportedGameBuild";

    public string Message => IsApproved
        ? $"{ContractId} is approved for game build {CurrentGameVersion}."
        : $"{ContractId} is blocked: current game build is {CurrentGameVersion}, but the contract was last approved for {ApprovedGameVersion}.";
}

internal static class GamePatchCompatibilityGate
{
    public static GamePatchCompatibility Evaluate(
        string contractId,
        string approvedGameVersion,
        string? currentGameVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedGameVersion);

        var current = string.IsNullOrWhiteSpace(currentGameVersion)
            ? ReadCurrentGameVersion()
            : currentGameVersion.Trim();
        var approved = approvedGameVersion.Trim();

        return new(
            contractId.Trim(),
            approved,
            current,
            string.Equals(current, approved, StringComparison.Ordinal));
    }

    private static string ReadCurrentGameVersion()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return "unknown";

        var versionPath = Path.Combine(
            Path.GetDirectoryName(processPath) ?? string.Empty,
            "ffxivgame.ver");
        try
        {
            if (File.Exists(versionPath))
            {
                var version = File.ReadAllText(versionPath).Trim();
                return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
            }

            return FileVersionInfo.GetVersionInfo(processPath).FileVersion?.Trim() is { Length: > 0 } fileVersion
                ? fileVersion
                : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
