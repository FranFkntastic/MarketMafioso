using Dalamud.Configuration;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.RetainerRestock;
using MarketMafioso.TradeQueue;
using MarketMafioso.WorkshopPrep;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Persistence;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MarketMafioso;

[Serializable]
public class Configuration : IPluginConfiguration
{
    private readonly object saveSync = new();
    private const string RuntimeStateFile = "runtime-state.json";
    private string? lastWrittenRuntimeJson;
    // Same Newtonsoft settings Dalamud uses for this file, so the moved state round-trips exactly as before.
    private static readonly JsonSerializerSettings RuntimeJsonSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };
    private bool savePending;
    private DateTime retrySaveAtUtc;
    private string pendingSaveOperation = string.Empty;
    private readonly PersistenceWarning saveWarning = new("plugin configuration");

    private TradeQueueTimingOptions? tradeQueueTiming = new();
    private TradeQueuePolicyOptions? tradeQueuePolicy = new();
    private PersistedMarketBoardSessionCircuitBreakerState? marketBoardSessionCircuitBreaker = new();

    public int Version { get; set; } = 1;

    private string serverUrl = "http://localhost:8080/inventory";
    internal event Action? ServerUrlChanged;
    public string ServerUrl
    {
        get => serverUrl;
        set
        {
            if (serverUrl == value) return;
            serverUrl = value;
            ServerUrlChanged?.Invoke();
        }
    }
    public string ApiKey { get; set; } = string.Empty;
    public string CommandPickupApiKey { get; set; } = string.Empty;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
    public PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
    public PersistedMarketAcquisitionRequestDocument? PreviousMarketAcquisitionRequestDocument { get; set; }
    [JsonProperty("OutfitterRouteExecutionStateJson")]
    public string? ExactAcquisitionRouteExecutionStateJson { get; set; }
    public string? ShardAcquisitionCheckpointStateJson { get; set; }
    public List<PersistedMarketAcquisitionWorkbenchComposition> MarketAcquisitionWorkbenchCompositions { get; set; } = [];
    public string? SelectedMarketAcquisitionWorkbenchCompositionId { get; set; }
    public PersistedMarketAcquisitionWorkbenchCompositionSource? MarketAcquisitionWorkbenchCompositionSource { get; set; }
    public bool EnableMarketAcquisition { get; set; } = false;
    public bool MarketAcquisitionLockedByUser { get; set; }
    public DateTime? MarketAcquisitionUnlockedAtUtc { get; set; }
    public bool EnableOpportunisticWorldChecks { get; set; } = true;
    public MarketAcquisitionRouteDiagnosticsLevel MarketAcquisitionRouteDiagnostics { get; set; } =
        MarketAcquisitionRouteDiagnosticsLevel.Summary;
    public bool MarketAcquisitionExhaustiveResearchMode { get; set; } = false;
    public bool MarketAcquisitionAethernetDeparture { get; set; } = true;
    /// <summary>Courier roster content id of the cross-regional shopper; 0 means none chosen.</summary>
    public ulong CrossRegionShopperContentId { get; set; }
    /// <summary>Exact carried gil the courier moves from primary to shopper before shopping; 0 skips the transfer.</summary>
    public int CrossRegionFundGil { get; set; }
    /// <summary>When the shopper's bags end a cross-regional cycle: haul, stow into the primary's retainers, and go back for the rest.</summary>
    public bool CrossRegionMultiCycle { get; set; } = true;
    public bool MarketAcquisitionRouteAlerts { get; set; } = true;
    public bool ArchiveCompletedMarketAcquisitionRouteDiagnostics { get; set; } = true;
    public int MarketAcquisitionRouteDiagnosticsHotDays { get; set; } = 14;
    public int MarketAcquisitionRouteDiagnosticsHotRuns { get; set; } = 50;
    [Obsolete("Use MarketAcquisitionRouteDiagnostics. Retained only to deserialize older configurations.")]
    public bool CreateMarketAcquisitionRouteDiagnosticPackages { get; set; } = false;
    public bool ShouldSerializeCreateMarketAcquisitionRouteDiagnosticPackages() => false;
    public bool EnableMarketAcquisitionDryRunTools { get; set; } = false;

    [JsonProperty("EnableRemoteMarketPurchase")]
    public bool EnableMarketListingPurchases { get; set; } = false;

    [JsonProperty("RemoteMarketRejectedTerritories")]
    public List<uint> MarketListingRejectedTerritories { get; set; } = [];
    public bool EnableAgentBridge { get; set; } = false;
    public bool EnableAgentBridgeScreenshots { get; set; } = false;
    public bool EnableAgentBridgeAudit { get; set; } = false;
    public string AgentBridgeAccessToken { get; set; } = string.Empty;
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;
    public int MarketAcquisitionRecentWorldTtlHours { get; set; } = 18;
    public int MarketAcquisitionMinimumStopValueGil { get; set; } = 5_000;
    public int MarketAcquisitionMinimumStopCoveragePercent { get; set; } = 2;
    public int MarketAcquisitionCoverageColumns { get; set; } = 3;
    public bool MarketAcquisitionIgnoreRecentWorldVisitsForSweep { get; set; } = false;
    [JsonProperty("MarketAcquisitionWorldVisits")]
    public List<PersistedMarketAcquisitionWorldVisit> LegacyMarketAcquisitionWorldVisits { get; set; } = [];
    // Retain the only durable copy if migration has not yet committed its separate store.
    public bool ShouldSerializeLegacyMarketAcquisitionWorldVisits() => LegacyMarketAcquisitionWorldVisits.Count > 0;
    public bool EnableWorkshopHostCraftQuotes { get; set; } = false;
    public bool EnableCraftArchitectManualFallback { get; set; } = false;
    public string CraftArchitectQuoteFilePath { get; set; } = string.Empty;
    public string SettingsSelectedPageId { get; set; } = "general.server";
    public List<string> SettingsExpandedFolderPaths { get; set; } = ["General", "Inventory Reporter", "Advanced"];

    public bool IncludeArmoury { get; set; } = false;
    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeEquipped { get; set; } = false;
    public bool IncludeSaddlebag { get; set; } = false;

    public bool IncludeItemNames { get; set; } = true;
    public bool IncludeCharacterInfo { get; set; } = true;
    public int? ServiceAccountNumber { get; set; }

    public bool EnableAutoSendTimer { get; set; } = false;
    public int AutoSendIntervalMinutes { get; set; } = 5;
    public bool EnableMarketDiagnostics { get; set; } = false;
    public bool EnableRetainerListingRefresh { get; set; } = false;
    public PersistedRetainerListingRefreshState RetainerListingRefresh { get; set; } = new();
    public PersistedMarketBoardSessionCircuitBreakerState MarketBoardSessionCircuitBreaker
    {
        get => marketBoardSessionCircuitBreaker ??= new();
        set => marketBoardSessionCircuitBreaker = value ?? new();
    }

    [JsonIgnore]
    public Dictionary<ulong, CachedRetainer> RetainerCache { get; set; } = new();

    [JsonProperty("RetainerCache")]
    private Dictionary<ulong, CachedRetainer>? LegacyRetainerCache
    {
        get => null;
        set
        {
            if (value is { Count: > 0 })
                RetainerCache = value;
        }
    }

    public bool ShouldSerializeLegacyRetainerCache() => false;

    public List<WorkshopPrepQueueItem> WorkshopPrepQueue { get; set; } = new();
    public List<TradeQueueItem> TradeQueueItems { get; set; } = new();
    /// <summary>When set, the Trade Queue only trades to this recipient and credits that share's handover ledger.</summary>
    public TradeQueueRecipientBinding? TradeQueueBinding { get; set; }
    public TradeQueueRecoveryState TradeQueueRecovery { get; set; } = new();
    public bool AutoAcceptIncomingTrades { get; set; }
    public TradeQueueTimingOptions TradeQueueTiming
    {
        get => tradeQueueTiming ??= new();
        set => tradeQueueTiming = value ?? new();
    }
    public TradeQueuePolicyOptions TradeQueuePolicy
    {
        get => tradeQueuePolicy ??= new();
        set => tradeQueuePolicy = value ?? new();
    }
    public List<WorkshopFrozenQueue> FrozenWorkshopQueues { get; set; } = new();
    public bool SplitWorkshopQueueAndMaterials { get; set; }
    public WorkshopAssemblyOrder WorkshopAssemblyOrder { get; set; } = WorkshopAssemblyOrder.Sequential;
    /// <summary>Share handover subtracts what the assembler already holds (from its host inventory report).</summary>
    public bool WorkshopHandoverCountTheirInventory { get; set; } = true;
    public bool WorkshopHandoverIncludeTheirRetainers { get; set; }
    /// <summary>Share handover and share restock leave min(bags, active prep queue need) in the sender's bags.</summary>
    public bool WorkshopHandoverKeepMyPlan { get; set; } = true;
    [JsonProperty("RetainerRestockPlanItems")]
    public List<RetainerRestockPlanItem> RetainerRestockPlanItems { get; set; } = new();

    public Dictionary<string, QuartermasterWorkshopRequestState> QuartermasterWorkshopRequests { get; set; } = new();
    public bool ShouldSerializeQuartermasterWorkshopRequests() => false;
    public bool AutomaticallyBuyWorkshopVendorMaterials { get; set; }
    public Dictionary<uint, int> WorkshopVendorApprovedQuantities { get; set; } = new();
    public List<uint> WorkshopVendorIncludedItems { get; set; } = new();
    public List<uint> WorkshopVendorExcludedItems { get; set; } = new();
    public GilVendorBuyRunSnapshot? ActiveWorkshopVendorBuyRun { get; set; }
    public WorkshopVendorRestockState? ActiveWorkshopVendorRestockState { get; set; }
    public bool ShouldSerializeActiveWorkshopVendorBuyRun() => false;
    public bool ShouldSerializeActiveWorkshopVendorRestockState() => false;
    public int WorkshopVendorRestockLegacyConversions { get; set; }
    [JsonProperty("ActiveWorkshopVendorRestock")]
    public PersistedWorkshopVendorRestockRun? LegacyActiveWorkshopVendorRestock { get; set; }
    public bool ShouldSerializeLegacyActiveWorkshopVendorRestock() => false;
    public Guid? ActiveFrozenWorkshopQueueId { get; set; }
    public List<uint> FavoriteWorkshopProjectIds { get; set; } = new();
    [JsonProperty("Squire")]
    public Newtonsoft.Json.Linq.JObject? LegacySquireConfiguration { get; set; }

    private sealed class RuntimeState
    {
        public required Dictionary<string, QuartermasterWorkshopRequestState> QuartermasterWorkshopRequests { get; init; }
        public required GilVendorBuyRunSnapshot? ActiveWorkshopVendorBuyRun { get; init; }
        public required WorkshopVendorRestockState? ActiveWorkshopVendorRestockState { get; init; }
    }

    internal void LoadRuntimeState(string directory)
    {
        var path = Path.Combine(directory, RuntimeStateFile);
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonConvert.DeserializeObject<RuntimeState>(json, RuntimeJsonSettings);
            if (state?.QuartermasterWorkshopRequests is null)
                throw new InvalidDataException("Missing workshop request dictionary.");

            QuartermasterWorkshopRequests = state.QuartermasterWorkshopRequests;
            ActiveWorkshopVendorBuyRun = state.ActiveWorkshopVendorBuyRun;
            ActiveWorkshopVendorRestockState = state.ActiveWorkshopVendorRestockState;
            lastWrittenRuntimeJson = SerializeRuntimeState();
        }
        catch (FileNotFoundException) { } // First save migrates the values loaded from settings.
        catch (DirectoryNotFoundException) { }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Could not load {Path}; retaining workshop state from settings.", path);
        }
    }

    private void SaveRuntimeState()
    {
        var json = SerializeRuntimeState();
        if (json == lastWrittenRuntimeJson) return;

        using var document = System.Text.Json.JsonDocument.Parse(json);
        AtomicJsonFile.Write(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), RuntimeStateFile), document.RootElement);
        lastWrittenRuntimeJson = json;
    }

    private string SerializeRuntimeState() =>
        JsonConvert.SerializeObject(new RuntimeState
        {
            QuartermasterWorkshopRequests = QuartermasterWorkshopRequests,
            ActiveWorkshopVendorBuyRun = ActiveWorkshopVendorBuyRun,
            ActiveWorkshopVendorRestockState = ActiveWorkshopVendorRestockState,
        }, RuntimeJsonSettings);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Save()
    {
        var caller = new StackFrame(1).GetMethod();
        SaveCore($"{caller?.DeclaringType?.Name}.{caller?.Name}", required: false);
    }

    /// <summary>For checkpoint callers whose existing failure handling must observe a failed write.</summary>
    public void SaveRequired(string operation) => SaveCore(operation, required: true);

    public void RetryPendingSave()
    {
        lock (saveSync)
        {
            try
            {
                if (savePending && DateTime.UtcNow >= retrySaveAtUtc)
                    SaveCore(pendingSaveOperation, required: false);
            }
            catch (Exception exception)
            {
                // Keep an unexpected retry failure from starving every framework tick.
                retrySaveAtUtc = DateTime.UtcNow.AddSeconds(5);
                saveWarning.Failed(pendingSaveOperation, exception, unexpected: true);
            }
        }
    }

    private void SaveCore(string operation, bool required)
    {
        lock (saveSync)
        {
            savePending = true;
            pendingSaveOperation = operation;
            if (!required && DateTime.UtcNow < retrySaveAtUtc)
                return;
            try
            {
                SaveRuntimeState(); // Commit the run state before dropping its legacy settings copy.
                Plugin.PluginInterface.SavePluginConfig(this);
                savePending = false;
                retrySaveAtUtc = default;
                saveWarning.Recovered(operation);
            }
            catch (Exception exception) when (PersistenceWarning.IsStorageFailure(exception))
            {
                retrySaveAtUtc = DateTime.UtcNow.AddSeconds(5);
                saveWarning.Failed(operation, exception);
                if (required) throw;
            }
            catch (Exception exception)
            {
                retrySaveAtUtc = DateTime.UtcNow.AddSeconds(5);
                saveWarning.Failed(operation, exception, unexpected: true);
                if (required) throw;
            }
        }
    }
}

/// <summary>Storage failures retain dirty state; diagnostics repeat at most once per minute per error.</summary>
internal sealed class PersistenceWarning(string destination)
{
    private readonly Dictionary<string, DateTime> reported = new();
    private bool failed;

    internal static bool IsStorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception ||
        (error.InnerException is { } inner && IsStorageFailure(inner));

    internal void Failed(string operation, Exception error, bool unexpected = false)
    {
        failed = true;
        var key = $"{error.GetType().FullName}:{error.Message}";
        if (reported.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        if (reported.Count >= 32) reported.Clear();
        reported[key] = DateTime.UtcNow;
        var message = $"[MarketMafioso] {operation}: {destination} persistence failed; in-memory state retained for retry.";
        if (unexpected) Plugin.Log.Error(error, message);
        else Plugin.Log.Warning(error, message);
    }

    internal void Recovered(string operation)
    {
        if (!failed) return;
        Plugin.Log.Information($"[MarketMafioso] {operation}: {destination} persistence recovered.");
        failed = false;
        reported.Clear();
    }
}

[Serializable]
public sealed class QuartermasterWorkshopRequestState
{
    public string? Code { get; set; }
    public ulong LocalContentId { get; set; }
    public uint HomeWorldId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? HomeWorldName { get; set; }
    public string Signature { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public DateTime SubmittedAtUtc { get; set; }
    public string? ProviderInstanceId { get; set; }
    public long? Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public List<QuartermasterWorkshopOperationReceipt> Receipts { get; set; } = new();
}

[Serializable]
public sealed class QuartermasterWorkshopOperationReceipt
{
    public long Revision { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public uint? ItemId { get; set; }
    public ulong? RetainerId { get; set; }
    public int? Quantity { get; set; }
}

[Serializable]
public class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public string? OwnerCharacterName { get; set; }
    public string? OwnerHomeWorld { get; set; }
    public DateTime LastUpdated { get; set; }
    public ulong Gil { get; set; }
    public List<CachedBag> Bags { get; set; } = new();
    public List<CachedMarketListing> MarketListings { get; set; } = new();
}

[Serializable]
public class CachedBag
{
    public string BagName { get; set; } = string.Empty;
    public string? Location { get; set; }
    public List<CachedItem> Items { get; set; } = new();
}

[Serializable]
public class CachedItem
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public float? ConditionPercent { get; set; }
    public bool? Equipped { get; set; }
}

[Serializable]
public class CachedMarketListing
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public float? ConditionPercent { get; set; }
    public uint? UnitPrice { get; set; }
    public string? ListedAt { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionClaim
{
    public string Id { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string? CreatedByPluginInstanceId { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint Quantity { get; set; }
    public string TargetBasis { get; set; } = string.Empty;
    public uint MaximumOverage { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint MaxTotalGil { get; set; }
    public string WorldMode { get; set; } = string.Empty;
    public List<string> SelectedWorlds { get; set; } = [];
    public string ClaimToken { get; set; } = string.Empty;
    public string? AcceptIdempotencyKey { get; set; }
    public string? RejectIdempotencyKey { get; set; }
    public List<PersistedMarketAcquisitionLine> Lines { get; set; } = [];
}

[Serializable]
public sealed class PersistedMarketAcquisitionLine
{
    public string LineId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public string TargetBasis { get; set; } = string.Empty;
    public uint MaximumOverage { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
    public string Status { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public string? LatestMessage { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionRequestDocument
{
    public uint RunSpendCap { get; set; }
    public string LocalRequestId { get; set; } = string.Empty;
    public int LocalRevision { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WorldMode { get; set; } = string.Empty;
    public string SweepScope { get; set; } = string.Empty;
    public List<string> SweepDataCenters { get; set; } = [];
    public List<PersistedMarketAcquisitionRequestLineDocument> Lines { get; set; } = [];
    public string? ExactAcquisitionAuthorityJson { get; set; }
    public string? RemoteRequestId { get; set; }
    public int RemoteRevision { get; set; }
    public string? RemoteOrigin { get; set; }
    public string? LastSyncedHash { get; set; }
    public string? RemoteHash { get; set; }
    public string? LastPlanHash { get; set; }
    public string SyncStatus { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionRequestLineDocument
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public string TargetBasis { get; set; } = string.Empty;
    public uint MaximumOverage { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionWorkbenchComposition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WorldMode { get; set; } = string.Empty;
    public string SweepScope { get; set; } = string.Empty;
    public List<string> SweepDataCenters { get; set; } = [];
    public List<PersistedMarketAcquisitionRequestLineDocument> Lines { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionWorkbenchCompositionSource
{
    public string CompositionId { get; set; } = string.Empty;
    public string WorkbenchRequestId { get; set; } = string.Empty;
    public bool LoadedFromComposition { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionWorldVisit
{
    public string WorldName { get; set; } = string.Empty;
    public string DataCenter { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public string Result { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public int ObservedLegalListingCount { get; set; }
    public uint ObservedLegalQuantity { get; set; }
    public ulong ObservedLegalGil { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? RouteRunId { get; set; }
    public string? RouteStopId { get; set; }
}
