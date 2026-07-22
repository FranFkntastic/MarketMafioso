using System;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.SquireIntegration;

public interface IStandaloneSquireIpcAdapter
{
    bool HasCapabilities { get; }
    bool HasSnapshot { get; }
    bool HasOpen { get; }
    string GetCapabilities();
    string GetSnapshot();
    string Open();
}

public sealed class DalamudStandaloneSquireIpcAdapter : IStandaloneSquireIpcAdapter
{
    private readonly ICallGateSubscriber<string> capabilities;
    private readonly ICallGateSubscriber<string> snapshot;
    private readonly ICallGateSubscriber<string> open;

    public DalamudStandaloneSquireIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        capabilities = pluginInterface.GetIpcSubscriber<string>(StandaloneSquireIpcClient.GetCapabilitiesChannel);
        snapshot = pluginInterface.GetIpcSubscriber<string>(StandaloneSquireIpcClient.GetSnapshotChannel);
        open = pluginInterface.GetIpcSubscriber<string>(StandaloneSquireIpcClient.OpenChannel);
    }

    public bool HasCapabilities => capabilities.HasFunction;
    public bool HasSnapshot => snapshot.HasFunction;
    public bool HasOpen => open.HasFunction;
    public string GetCapabilities() => capabilities.InvokeFunc();
    public string GetSnapshot() => snapshot.InvokeFunc();
    public string Open() => open.InvokeFunc();
}

public sealed record StandaloneSquireSnapshot(
    string ProviderInstanceId,
    string FeatureState,
    bool WindowOpen,
    string Workspace,
    int CleanupRuleCount,
    int CharacterRuleCount,
    DateTimeOffset? LegacyMmfImportedAtUtc);

public sealed class StandaloneSquireIpcClient
{
    public const string GetCapabilitiesChannel = "Squire.v1.GetCapabilities";
    public const string GetSnapshotChannel = "Squire.v1.GetSnapshot";
    public const string OpenChannel = "Squire.v1.Open";
    public const string CapabilitiesSchema = "gooseworks-squire-capabilities/v1";
    public const string SnapshotSchema = "gooseworks-squire-snapshot/v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IStandaloneSquireIpcAdapter adapter;

    public StandaloneSquireIpcClient(IStandaloneSquireIpcAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public bool TryGetSnapshot(out StandaloneSquireSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = string.Empty;
        try
        {
            if (!adapter.HasCapabilities || !adapter.HasSnapshot)
            {
                error = "Standalone Squire is not loaded.";
                return false;
            }
            var capabilities = JsonSerializer.Deserialize<CapabilitiesWire>(adapter.GetCapabilities(), JsonOptions);
            if (capabilities is null || capabilities.Schema != CapabilitiesSchema ||
                string.IsNullOrWhiteSpace(capabilities.ProviderInstanceId) ||
                capabilities.Capabilities?.Contains("standalone-plugin", StringComparer.Ordinal) != true)
            {
                error = "Standalone Squire returned unsupported capabilities.";
                return false;
            }
            var wire = JsonSerializer.Deserialize<SnapshotWire>(adapter.GetSnapshot(), JsonOptions);
            if (wire is null || wire.Schema != SnapshotSchema ||
                !string.Equals(wire.ProviderInstanceId, capabilities.ProviderInstanceId, StringComparison.Ordinal))
            {
                error = "Standalone Squire returned an invalid snapshot.";
                return false;
            }
            snapshot = new(
                wire.ProviderInstanceId,
                wire.FeatureState ?? string.Empty,
                wire.WindowOpen,
                wire.Workspace ?? string.Empty,
                wire.CleanupRuleCount,
                wire.CharacterRuleCount,
                wire.LegacyMmfImportedAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            error = $"Standalone Squire IPC failed: {exception.Message}";
            return false;
        }
    }

    public bool TryOpen(out string error)
    {
        error = string.Empty;
        try
        {
            if (!adapter.HasOpen)
            {
                error = "Standalone Squire does not expose its open-window IPC call.";
                return false;
            }
            _ = adapter.Open();
            return true;
        }
        catch (InvalidOperationException exception)
        {
            error = $"Standalone Squire could not be opened: {exception.Message}";
            return false;
        }
    }

    private sealed class CapabilitiesWire
    {
        public string? Schema { get; init; }
        public string? ProviderInstanceId { get; init; }
        public string[]? Capabilities { get; init; }
    }

    private sealed class SnapshotWire
    {
        public string? Schema { get; init; }
        public string ProviderInstanceId { get; init; } = string.Empty;
        public string? FeatureState { get; init; }
        public bool WindowOpen { get; init; }
        public string? Workspace { get; init; }
        public int CleanupRuleCount { get; init; }
        public int CharacterRuleCount { get; init; }
        public DateTimeOffset? LegacyMmfImportedAtUtc { get; init; }
    }
}
