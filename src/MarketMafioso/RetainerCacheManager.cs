using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso;

/// <summary>
/// Hooks the game's retainer inventory addon and replaces a cache entry only
/// after a complete, identity-correlated inventory capture is saved.
/// </summary>
public class RetainerCacheManager : IDisposable, IRetainerCacheInvalidator
{
    private static readonly InventoryType[] RequiredOrdinaryRetainerContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private readonly IPlayerState playerState;
    private readonly RetainerCacheFileStore? cacheStore;
    private bool isBatchRefreshActive;
    private long captureSequence;
    private readonly List<RetainerCaptureReceipt> recentReceipts = [];
    private RetainerCaptureSession? activeSession;

    // Both addon names are registered so the handler fires regardless of
    // which layout the game uses (depends on player's bag count / resolution).
    private const string LargeAddon = "InventoryRetainerLarge";
    private const string SmallAddon = "InventoryRetainer";

    /// <summary>Raised after a retainer cache entry has been saved successfully.</summary>
    public event Action? RetainerCached;

    /// <summary>Raised for every terminal capture outcome, including rejected captures.</summary>
    public event Action<RetainerCaptureReceipt>? CaptureCompleted;

    public RetainerCaptureCheckpoint CaptureCheckpoint => new(captureSequence);
    public RetainerCaptureReceipt? LastCaptureReceipt { get; private set; }
    public RetainerCaptureSession? ActiveSession => activeSession;

    /// <summary>
    /// Captures the active retainer identity and receipt checkpoint as one framework-thread
    /// observation. Consumers must use this snapshot before initiating the UI action that
    /// closes the retainer inventory.
    /// </summary>
    public RetainerCaptureWaitSnapshot GetCaptureWaitSnapshot() =>
        new(activeSession, new RetainerCaptureCheckpoint(captureSequence));

    public IReadOnlyList<RetainerCaptureReceipt> GetCaptureReceiptsAfter(RetainerCaptureCheckpoint checkpoint) =>
        recentReceipts.Where(receipt => receipt.Checkpoint.Value > checkpoint.Value).ToArray();

    public RetainerCacheManager(
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Configuration config,
        InventoryScanner scanner,
        HttpReporter reporter,
        IPlayerState playerState,
        RetainerCacheFileStore? cacheStore = null)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.config = config;
        this.scanner = scanner;
        this.reporter = reporter;
        this.playerState = playerState;
        this.cacheStore = cacheStore;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }

    private unsafe void OnRetainerWindowOpen(AddonEvent type, AddonArgs args)
    {
        try
        {
            var activeRetainer = ReadActiveRetainerIdentity();
            if (activeRetainer == null)
            {
                log.Warning("[MarketMafioso] Retainer window opened but no active retainer was found.");
                activeSession = null;
                return;
            }

            activeSession = new RetainerCaptureSession(
                activeRetainer.Value.RetainerId,
                activeRetainer.Value.RetainerName,
                GetCurrentOwnerScope());
            log.Debug($"[MarketMafioso] Retainer window opened for '{activeSession.RetainerName}' (id={activeSession.RetainerId})");
        }
        catch (Exception ex)
        {
            activeSession = null;
            log.Error(ex, "[MarketMafioso] Error in OnRetainerWindowOpen");
        }
    }

    private void OnRetainerWindowClose(AddonEvent type, AddonArgs args)
    {
        var session = activeSession;
        try
        {
            if (session == null)
            {
                PublishReceipt(RetainerCaptureOutcome.InvalidSession, null,
                    "Retainer window closed without a stable open-session identity; prior cache evidence was preserved.");
                return;
            }

            var closeIdentity = ReadActiveRetainerIdentity();
            if (closeIdentity == null)
            {
                PublishReceipt(RetainerCaptureOutcome.InvalidSession, session,
                    $"Retainer inventory capture for '{session.RetainerName}' could not verify the active retainer at close; prior cache evidence was preserved.");
                return;
            }

            if (closeIdentity.Value.RetainerId != session.RetainerId)
            {
                PublishReceipt(RetainerCaptureOutcome.IdentityMismatch, session,
                    $"Retainer inventory capture identity mismatch: opened {session.RetainerId}, closed {closeIdentity.Value.RetainerId}; prior cache evidence was preserved.");
                return;
            }

            var closeOwnerScope = GetCurrentOwnerScope();
            if (closeOwnerScope != session.OwnerScope)
            {
                PublishReceipt(RetainerCaptureOutcome.OwnerMismatch, session,
                    $"Retainer inventory capture owner changed while '{session.RetainerName}' was open; prior cache evidence was preserved.");
                return;
            }

            var capture = scanner.CaptureCurrentRetainer(config);
            var decision = EvaluateCapture(capture);
            if (!decision.CanReplace)
            {
                PublishReceipt(RetainerCaptureOutcome.Incomplete, session,
                    $"Retainer inventory capture for '{session.RetainerName}' was incomplete; missing required pages: {FormatContainers(decision.MissingRequiredContainers)}. Prior cache evidence was preserved.");
                return;
            }

            var hadPrevious = config.RetainerCache.TryGetValue(session.RetainerId, out var previous);
            var cachedRetainer = BuildCachedRetainer(session, capture, previous);
            if (cacheStore == null)
            {
                PublishReceipt(RetainerCaptureOutcome.PersistenceFailed, session,
                    $"Retainer inventory capture for '{session.RetainerName}' was complete but no cache store is available; prior cache evidence was preserved.");
                return;
            }

            config.RetainerCache[session.RetainerId] = cachedRetainer;
            try
            {
                cacheStore.Save(config.RetainerCache);
            }
            catch (Exception ex)
            {
                if (hadPrevious)
                    config.RetainerCache[session.RetainerId] = previous!;
                else
                    config.RetainerCache.Remove(session.RetainerId);

                log.Error(ex, "[MarketMafioso] Error saving retainer inventory cache");
                PublishReceipt(RetainerCaptureOutcome.PersistenceFailed, session,
                    $"Retainer inventory capture for '{session.RetainerName}' could not be saved; prior cache evidence was preserved.");
                return;
            }

            var totalItems = cachedRetainer.Bags.Sum(bag => bag.Items.Count);
            log.Information(
                $"[MarketMafioso] Cached retainer '{session.RetainerName}' - {totalItems} item(s) across {cachedRetainer.Bags.Count} bag(s).");
            PublishReceipt(RetainerCaptureOutcome.Persisted, session,
                $"Retainer inventory capture for '{session.RetainerName}' was saved.");
            PublishSubscribersSafely(
                RetainerCached,
                exception => LogSubscriberException(nameof(RetainerCached), exception));

            if (config.AutoSendOnRetainerClose && !isBatchRefreshActive)
                _ = reporter.SendReportAsync();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Error caching retainer inventory");
            PublishReceipt(RetainerCaptureOutcome.Failed, session,
                $"Retainer inventory capture failed for '{session?.RetainerName ?? "unknown retainer"}'; prior cache evidence was preserved.");
        }
        finally
        {
            activeSession = null;
        }
    }

    public RetainerCacheInvalidationResult InvalidateAndSave(ulong retainerId)
    {
        if (retainerId == 0)
            return new(RetainerCacheInvalidationOutcome.InvalidRetainerId, "Cannot invalidate retainer cache evidence without a retainer ID.");

        if (!config.RetainerCache.Remove(retainerId))
            return new(RetainerCacheInvalidationOutcome.NotFound, $"No cached evidence exists for retainer {retainerId}.");

        if (cacheStore == null)
            return new(RetainerCacheInvalidationOutcome.PersistenceFailed,
                $"Removed cached evidence for retainer {retainerId}, but no cache store is available to persist the invalidation.");

        try
        {
            cacheStore.Save(config.RetainerCache);
            return new(RetainerCacheInvalidationOutcome.Removed,
                $"Removed and saved cached evidence for retainer {retainerId}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[MarketMafioso] Unable to save retainer cache invalidation for {retainerId}");
            return new(RetainerCacheInvalidationOutcome.PersistenceFailed,
                $"Removed cached evidence for retainer {retainerId}, but the invalidation could not be saved.");
        }
    }

    public static RetainerCaptureDecision EvaluateCapture(RetainerInventoryCaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var missing = RequiredOrdinaryRetainerContainers
            .Where(container => !capture.LoadedContainers.Contains(container))
            .ToArray();
        return new RetainerCaptureDecision(missing.Length == 0, missing);
    }

    public static RetainerCaptureReceiptMatch EvaluateReceipt(
        RetainerCaptureReceipt? receipt,
        ulong expectedRetainerId,
        RetainerCaptureCheckpoint checkpoint)
    {
        if (receipt == null || receipt.Checkpoint.Value <= checkpoint.Value)
            return RetainerCaptureReceiptMatch.Pending;
        if (receipt.RetainerId != expectedRetainerId)
            return RetainerCaptureReceiptMatch.IdentityMismatch;

        return receipt.Outcome switch
        {
            RetainerCaptureOutcome.Persisted => RetainerCaptureReceiptMatch.Persisted,
            RetainerCaptureOutcome.Incomplete => RetainerCaptureReceiptMatch.Incomplete,
            RetainerCaptureOutcome.PersistenceFailed => RetainerCaptureReceiptMatch.PersistenceFailed,
            _ => RetainerCaptureReceiptMatch.Invalid,
        };
    }

    public static RetainerCaptureReceiptMatch EvaluateReceipt(
        RetainerCaptureReceipt? receipt,
        RetainerCaptureWaitSnapshot captureWait)
    {
        return captureWait.Session == null
            ? RetainerCaptureReceiptMatch.Pending
            : EvaluateReceipt(receipt, captureWait.Session.RetainerId, captureWait.Checkpoint);
    }

    public void BeginBatchRefresh()
    {
        isBatchRefreshActive = true;
    }

    public void EndBatchRefresh()
    {
        isBatchRefreshActive = false;
    }

    private unsafe RetainerIdentity? ReadActiveRetainerIdentity()
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return null;

        var activeRetainer = manager->GetActiveRetainer();
        if (activeRetainer == null || activeRetainer->RetainerId == 0)
            return null;

        fixed (byte* namePtr = activeRetainer->Name)
        {
            var name = Marshal.PtrToStringUTF8((nint)namePtr, 32)?.Split('\0')[0] ?? string.Empty;
            return new RetainerIdentity(activeRetainer->RetainerId, name);
        }
    }

    internal static CachedRetainer BuildCachedRetainer(
        RetainerCaptureSession session,
        RetainerInventoryCaptureResult capture,
        CachedRetainer? previous,
        DateTime? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(capture);

        return new CachedRetainer
        {
            RetainerId = session.RetainerId,
            RetainerName = session.RetainerName,
            OwnerCharacterName = session.OwnerScope.CharacterName,
            OwnerHomeWorld = session.OwnerScope.HomeWorld,
            LastUpdated = capturedAtUtc ?? DateTime.UtcNow,
            // A zero is meaningful when the source was loaded. For unobserved optional
            // containers, retain prior evidence; a newly cached retainer falls back to
            // the model's existing unknown-compatible defaults without claiming a read.
            Gil = capture.ObservedGil ?? previous?.Gil ?? 0,
            Bags = capture.Bags.Select(bag => new CachedBag
            {
                BagName = bag.BagName,
                Location = bag.Location,
                Items = bag.Items.Select(item => new CachedItem
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    Quantity = item.Quantity,
                    IsHQ = item.IsHQ,
                    Condition = item.Condition,
                    ContainerKey = item.ContainerKey,
                    SlotIndex = item.SlotIndex,
                    ConditionPercent = item.ConditionPercent,
                    Equipped = item.Equipped,
                }).ToList(),
            }).ToList(),
            MarketListings = capture.ObservedMarketListings is { } observedMarketListings
                ? MapMarketListings(observedMarketListings)
                : previous?.MarketListings.Select(CopyMarketListing).ToList() ?? [],
        };
    }

    private static List<CachedMarketListing> MapMarketListings(IEnumerable<RetainerMarketListing> listings) =>
        listings.Select(item => new CachedMarketListing
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHQ,
            Condition = item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = item.ConditionPercent,
            UnitPrice = item.UnitPrice,
            ListedAt = item.ListedAt,
        }).ToList();

    private static CachedMarketListing CopyMarketListing(CachedMarketListing item) =>
        new()
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHQ,
            Condition = item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = item.ConditionPercent,
            UnitPrice = item.UnitPrice,
            ListedAt = item.ListedAt,
        };

    private void PublishReceipt(RetainerCaptureOutcome outcome, RetainerCaptureSession? session, string message)
    {
        var receipt = new RetainerCaptureReceipt(
            new RetainerCaptureCheckpoint(++captureSequence),
            session?.RetainerId ?? 0,
            session?.OwnerScope,
            outcome,
            message,
            DateTime.UtcNow);
        LastCaptureReceipt = receipt;
        recentReceipts.Add(receipt);
        if (recentReceipts.Count > 32)
            recentReceipts.RemoveAt(0);

        PublishSubscribersSafely(
            CaptureCompleted,
            receipt,
            exception => LogSubscriberException(nameof(CaptureCompleted), exception));
    }

    internal static void PublishSubscribersSafely<T>(
        Action<T>? subscribers,
        T value,
        Action<Exception> logException)
    {
        if (subscribers == null)
            return;

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception exception)
            {
                try
                {
                    logException(exception);
                }
                catch
                {
                    // A faulty diagnostic sink must not let a subscriber escape an addon lifecycle callback.
                }
            }
        }
    }

    internal static void PublishSubscribersSafely(Action? subscribers, Action<Exception> logException)
    {
        if (subscribers == null)
            return;

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                try
                {
                    logException(exception);
                }
                catch
                {
                    // A faulty diagnostic sink must not let a subscriber escape an addon lifecycle callback.
                }
            }
        }
    }

    private void LogSubscriberException(string eventName, Exception exception)
    {
        try
        {
            log.Error(exception, $"[MarketMafioso] Retainer cache {eventName} subscriber failed");
        }
        catch
        {
            // Publication must remain terminal even when logging infrastructure is unavailable.
        }
    }

    private RetainerOwnerScope GetCurrentOwnerScope() =>
        new(
            playerState.CharacterName,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);

    private static string FormatContainers(IReadOnlyList<InventoryType> containers) =>
        containers.Count == 0 ? "none" : string.Join(", ", containers);

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }

    private readonly record struct RetainerIdentity(ulong RetainerId, string RetainerName);
}

public interface IRetainerCacheInvalidator
{
    RetainerCacheInvalidationResult InvalidateAndSave(ulong retainerId);
}

public enum RetainerCacheInvalidationOutcome
{
    Removed,
    NotFound,
    InvalidRetainerId,
    PersistenceFailed,
}

public sealed record RetainerCacheInvalidationResult(
    RetainerCacheInvalidationOutcome Outcome,
    string Message)
{
    public bool Removed => Outcome == RetainerCacheInvalidationOutcome.Removed;
}

public readonly record struct RetainerCaptureCheckpoint(long Value);

public sealed record RetainerCaptureSession(
    ulong RetainerId,
    string RetainerName,
    RetainerOwnerScope OwnerScope);

/// <summary>Atomic framework-thread observation used to fence a retainer-close receipt wait.</summary>
public sealed record RetainerCaptureWaitSnapshot(
    RetainerCaptureSession? Session,
    RetainerCaptureCheckpoint Checkpoint);

public enum RetainerCaptureOutcome
{
    Persisted,
    Incomplete,
    IdentityMismatch,
    OwnerMismatch,
    InvalidSession,
    PersistenceFailed,
    Failed,
}

public sealed record RetainerCaptureReceipt(
    RetainerCaptureCheckpoint Checkpoint,
    ulong RetainerId,
    RetainerOwnerScope? OwnerScope,
    RetainerCaptureOutcome Outcome,
    string Message,
    DateTime OccurredAtUtc);

public sealed record RetainerCaptureDecision(
    bool CanReplace,
    IReadOnlyList<InventoryType> MissingRequiredContainers);

public enum RetainerCaptureReceiptMatch
{
    Pending,
    Persisted,
    IdentityMismatch,
    Incomplete,
    PersistenceFailed,
    Invalid,
}
