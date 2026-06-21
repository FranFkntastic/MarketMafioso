using Dalamud.Plugin.Services;
using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using System;
using System.Collections.Generic;

namespace MarketMafioso.Services;

public sealed unsafe class RetainerMarketCaptureService : IDisposable
{
    private const int MaxAttemptsPerItem = 2;
    private static readonly TimeSpan FirstAttemptTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RetryAttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettleWindowFast = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SettleWindowNormal = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan SettleWindowSlow = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MinInterRequestDelay = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan MaxInterRequestDelay = TimeSpan.FromMilliseconds(310);

    private readonly ActiveRetainerCaptureService _activeRetainerCaptureService;
    private readonly RetainerSnapshotStore _retainerSnapshotStore;
    private readonly RetainerMarketSnapshotStore _retainerMarketSnapshotStore;
    private readonly IMarketBoard _marketBoard;
    private readonly IPluginLog _pluginLog;
    private readonly object _sync = new();

    private CaptureRun? _run;

    public RetainerMarketCaptureService(
        ActiveRetainerCaptureService activeRetainerCaptureService,
        RetainerSnapshotStore retainerSnapshotStore,
        RetainerMarketSnapshotStore retainerMarketSnapshotStore,
        IMarketBoard marketBoard,
        IPluginLog pluginLog)
    {
        _activeRetainerCaptureService = activeRetainerCaptureService;
        _retainerSnapshotStore = retainerSnapshotStore;
        _retainerMarketSnapshotStore = retainerMarketSnapshotStore;
        _marketBoard = marketBoard;
        _pluginLog = pluginLog;

        _marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _run != null;
            }
        }
    }

    public string StatusMessage { get; private set; } = "Idle";

    public void Dispose()
    {
        _marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    public void StartActiveRetainerCaptureCycle()
    {
        lock (_sync)
        {
            if (_run != null)
            {
                throw new InvalidOperationException("A market capture cycle is already running.");
            }

            var captureResult = _activeRetainerCaptureService.CaptureActiveRetainer();
            if (captureResult.Snapshot == null)
            {
                throw new InvalidOperationException(captureResult.Message);
            }

            _retainerSnapshotStore.Upsert(captureResult.Snapshot);

            var targets = BuildTargets(captureResult.Snapshot.Listings);
            if (targets.Count == 0)
            {
                _retainerMarketSnapshotStore.Upsert(new RetainerMarketSnapshot(
                    captureResult.Snapshot.RetainerId,
                    captureResult.Snapshot.RetainerName,
                    DateTimeOffset.Now,
                    []));
                StatusMessage = $"Captured {captureResult.Snapshot.RetainerName}: no listed items to query.";
                _pluginLog.Information($"[MarketMafioso] {StatusMessage}");
                return;
            }

            _run = new CaptureRun(captureResult.Snapshot, targets);
            ScheduleCurrentRequest(_run, immediate: true);
        }
    }

    public void Tick()
    {
        lock (_sync)
        {
            if (_run == null)
            {
                return;
            }

            try
            {
                if (_run.IsRequestPendingDispatch)
                {
                    if (DateTimeOffset.Now < _run.NextRequestDispatchAt)
                    {
                        return;
                    }

                    BeginCurrentRequest(_run);
                    return;
                }

                TickCurrentRequest(_run);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Market capture failed: {ex.Message}";
                _pluginLog.Error(ex, "[MarketMafioso] Market capture cycle failed.");
                _run = null;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _run = null;
            StatusMessage = "Idle";
        }
    }

    private void TickCurrentRequest(CaptureRun run)
    {
        var proxy = RequireItemSearchProxy();
        var page = (InfoProxyPageInterface*)proxy;
        var target = run.Targets[run.CurrentTargetIndex];

        if (proxy->SearchItemId != target.ItemId)
        {
            throw new InvalidOperationException(
                $"Item search proxy moved to item id {proxy->SearchItemId} while waiting for {target.ItemId}.");
        }

        ObserveSignals(run, page, proxy);

        if (run.ReceivedMatchingListings)
        {
            var settleWindow = GetAdaptiveSettleWindow(run);
            var settled = DateTimeOffset.Now - run.LastMatchingListingAt >= settleWindow;
            if (settled)
            {
                CompleteCurrentItem(run, target, BuildOrderedListings(run));
                return;
            }
        }

        var attemptTimeout = GetAttemptTimeout(run.CurrentAttempt);
        var elapsed = DateTimeOffset.Now - run.RequestStartedAt;
        if (elapsed <= attemptTimeout)
        {
            return;
        }

        var sawRequestActivity = run.SawRequestIdChange || run.SawWaitingForListings || run.SawListingCountChange;
        if (run.ReceivedMatchingListings)
        {
            var partialListings = BuildOrderedListings(run);
            var warning =
                $"Timed out waiting for additional market pages for item {target.ItemName} ({target.ItemId}); storing {partialListings.Count} listings.";
            _pluginLog.Warning($"[MarketMafioso] {warning}");
            CompleteCurrentItem(run, target, partialListings);
            return;
        }

        if (sawRequestActivity)
        {
            RetryCurrentItemOrFail(
                run,
                target,
                $"No market listings were received for item {target.ItemName} ({target.ItemId}) on attempt {run.CurrentAttempt}. Manual verification recommended.");
            return;
        }

        RetryCurrentItemOrFail(
            run,
            target,
            $"Timed out waiting for market request activity for item id {target.ItemId}. " +
            $"attemptTimeoutMs={attemptTimeout.TotalMilliseconds:0}, " +
            $"requestId startCurrent={run.StartCurrentRequestId}, startNext={run.StartNextRequestId}, " +
            $"current={page->CurrentRequestId}, next={page->NextRequestId}, " +
            $"waiting={proxy->WaitingForListings}, listingCount={proxy->ListingCount}, " +
            $"sawRequestIdChange={run.SawRequestIdChange}, " +
            $"sawWaitingForListings={run.SawWaitingForListings}, " +
            $"sawListingCountChange={run.SawListingCountChange}.");
    }

    private void BeginCurrentRequest(CaptureRun run)
    {
        var target = run.Targets[run.CurrentTargetIndex];
        var proxy = RequireItemSearchProxy();
        var page = (InfoProxyPageInterface*)proxy;
        var infoProxy = (InfoProxyInterface*)proxy;

        run.StartCurrentRequestId = page->CurrentRequestId;
        run.StartNextRequestId = page->NextRequestId;
        run.ObservedCurrentRequestId = page->CurrentRequestId;
        run.ObservedNextRequestId = page->NextRequestId;
        run.PreviousListingCount = proxy->ListingCount;
        run.SawRequestIdChange = false;
        run.SawWaitingForListings = false;
        run.SawListingCountChange = false;
        run.ReceivedMatchingListings = false;
        run.MatchingOfferingEvents = 0;
        run.CurrentAttempt++;
        run.IsRequestPendingDispatch = false;
        run.RequestStartedAt = DateTimeOffset.Now;
        run.LastMatchingListingAt = run.RequestStartedAt;
        run.PendingListings.Clear();
        run.SeenListingIdentities.Clear();

        proxy->SearchItemId = target.ItemId;
        infoProxy->ClearListData();
        run.PreviousListingCount = proxy->ListingCount;

        if (!infoProxy->RequestData())
        {
            RetryCurrentItemOrFail(
                run,
                target,
                $"RequestData() returned false for item id {target.ItemId} on attempt {run.CurrentAttempt}.");
            return;
        }

        ObserveSignals(run, page, proxy);

        StatusMessage =
            $"Requesting market data {run.CurrentTargetIndex + 1}/{run.Targets.Count}: {target.ItemName} (attempt {run.CurrentAttempt}/{MaxAttemptsPerItem})";
        _pluginLog.Information($"[MarketMafioso] {StatusMessage}");
    }

    private void RetryCurrentItemOrFail(CaptureRun run, TargetItem target, string reason)
    {
        if (run.CurrentAttempt < MaxAttemptsPerItem)
        {
            var delay = GetRandomInterRequestDelay();
            _pluginLog.Warning(
                $"[MarketMafioso] {reason} Retrying {target.ItemName} ({target.ItemId}) " +
                $"attempt {run.CurrentAttempt + 1}/{MaxAttemptsPerItem} after {delay.TotalMilliseconds:0}ms.");
            ScheduleCurrentRequest(run, immediate: false, delay);
            return;
        }

        run.FailedItems.Add(new FailedItem(target.ItemId, target.ItemName, reason));
        _pluginLog.Warning(
            $"[MarketMafioso] Failed to capture market data for {target.ItemName} ({target.ItemId}) " +
            $"after {run.CurrentAttempt}/{MaxAttemptsPerItem} attempts. Moving on.");
        CompleteCurrentItem(run, target, null);
    }

    private void CompleteCurrentItem(CaptureRun run, TargetItem target, List<MarketListingSnapshot>? listings)
    {
        if (listings != null)
        {
            run.CompletedItems.Add(new ItemMarketSnapshot(target.ItemId, target.ItemName, listings));
        }

        run.CurrentTargetIndex++;
        run.CurrentAttempt = 0;
        if (run.CurrentTargetIndex >= run.Targets.Count)
        {
            run.CompletedItems.Sort((left, right) => string.CompareOrdinal(left.ItemName, right.ItemName));

            var marketSnapshot = new RetainerMarketSnapshot(
                run.RetainerSnapshot.RetainerId,
                run.RetainerSnapshot.RetainerName,
                DateTimeOffset.Now,
                run.CompletedItems);
            _retainerMarketSnapshotStore.Upsert(marketSnapshot);

            StatusMessage =
                $"Captured listings + market data for {run.RetainerSnapshot.RetainerName} ({run.CompletedItems.Count} items, failed: {run.FailedItems.Count}).";
            _pluginLog.Information($"[MarketMafioso] {StatusMessage}");
            foreach (var failedItem in run.FailedItems)
            {
                _pluginLog.Warning(
                    $"[MarketMafioso] Failed fetch: {failedItem.ItemName} ({failedItem.ItemId}). " +
                    $"Last reason: {failedItem.Reason}");
            }
            _run = null;
            return;
        }

        var delay = GetRandomInterRequestDelay();
        ScheduleCurrentRequest(run, immediate: false, delay);
    }

    private void ScheduleCurrentRequest(CaptureRun run, bool immediate, TimeSpan? explicitDelay = null)
    {
        run.IsRequestPendingDispatch = true;
        if (immediate)
        {
            run.NextRequestDispatchAt = DateTimeOffset.Now;
            return;
        }

        var delay = explicitDelay ?? GetRandomInterRequestDelay();
        run.NextRequestDispatchAt = DateTimeOffset.Now + delay;
    }

    private static TimeSpan GetRandomInterRequestDelay()
    {
        var minMs = (int)MinInterRequestDelay.TotalMilliseconds;
        var maxMs = (int)MaxInterRequestDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs + 1));
    }

    private static TimeSpan GetAttemptTimeout(int currentAttempt)
    {
        return currentAttempt <= 1 ? FirstAttemptTimeout : RetryAttemptTimeout;
    }

    private static TimeSpan GetAdaptiveSettleWindow(CaptureRun run)
    {
        if (run.PendingListings.Count >= 20)
        {
            return SettleWindowFast;
        }

        if (run.MatchingOfferingEvents >= 2 || run.PendingListings.Count >= 10)
        {
            return SettleWindowNormal;
        }

        return SettleWindowSlow;
    }

    private static unsafe InfoProxyItemSearch* RequireItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
        {
            throw new InvalidOperationException("InfoModule is unavailable.");
        }

        var proxyInterface = infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        if (proxyInterface == null)
        {
            throw new InvalidOperationException("InfoProxyItemSearch is unavailable.");
        }

        return (InfoProxyItemSearch*)proxyInterface;
    }

    private static List<MarketListingSnapshot> BuildOrderedListings(CaptureRun run)
    {
        var list = new List<MarketListingSnapshot>(run.PendingListings);

        list.Sort((left, right) => left.UnitPrice.CompareTo(right.UnitPrice));
        return list;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
        lock (_sync)
        {
            if (_run == null)
            {
                return;
            }

            var run = _run;
            var target = run.Targets[run.CurrentTargetIndex];
            var now = DateTimeOffset.Now;
            var addedAny = false;

            foreach (var listing in currentOfferings.ItemListings)
            {
                if (listing.ItemId != target.ItemId)
                {
                    continue;
                }

                var identity = new ListingIdentity(
                    listing.ListingId,
                    listing.RetainerId,
                    listing.PricePerUnit,
                    listing.ItemQuantity,
                    listing.IsHq,
                    (byte)listing.RetainerCityId,
                    listing.TotalTax);
                if (!run.SeenListingIdentities.Add(identity))
                {
                    continue;
                }

                run.PendingListings.Add(new MarketListingSnapshot(
                    listing.ListingId,
                    listing.RetainerId,
                    listing.RetainerName,
                    listing.PricePerUnit,
                    listing.ItemQuantity,
                    listing.IsHq,
                    (byte)listing.RetainerCityId,
                    listing.TotalTax));
                addedAny = true;
            }

            if (addedAny)
            {
                run.ReceivedMatchingListings = true;
                run.LastMatchingListingAt = now;
                run.MatchingOfferingEvents++;
            }
        }
    }

    private static void ObserveSignals(CaptureRun run, InfoProxyPageInterface* page, InfoProxyItemSearch* proxy)
    {
        if (page->CurrentRequestId != run.ObservedCurrentRequestId
            || page->NextRequestId != run.ObservedNextRequestId)
        {
            run.SawRequestIdChange = true;
            run.ObservedCurrentRequestId = page->CurrentRequestId;
            run.ObservedNextRequestId = page->NextRequestId;
        }

        if (proxy->WaitingForListings)
        {
            run.SawWaitingForListings = true;
        }

        if (proxy->ListingCount != run.PreviousListingCount)
        {
            run.SawListingCountChange = true;
            run.PreviousListingCount = proxy->ListingCount;
        }
    }

    private static List<TargetItem> BuildTargets(IReadOnlyList<RetainerListingSnapshot> listings)
    {
        var targets = new List<TargetItem>();
        var seenItemIds = new HashSet<uint>();

        foreach (var listing in listings)
        {
            if (!seenItemIds.Add(listing.ItemId))
            {
                continue;
            }

            targets.Add(new TargetItem(listing.ItemId, listing.ItemName));
        }

        targets.Sort((left, right) => string.CompareOrdinal(left.ItemName, right.ItemName));
        return targets;
    }

    private sealed class CaptureRun
    {
        public CaptureRun(RetainerSnapshot retainerSnapshot, IReadOnlyList<TargetItem> targets)
        {
            RetainerSnapshot = retainerSnapshot;
            Targets = targets;
        }

        public RetainerSnapshot RetainerSnapshot { get; }

        public IReadOnlyList<TargetItem> Targets { get; }

        public List<ItemMarketSnapshot> CompletedItems { get; } = [];

        public List<FailedItem> FailedItems { get; } = [];

        public int CurrentTargetIndex { get; set; }

        public int CurrentAttempt { get; set; }

        public bool IsRequestPendingDispatch { get; set; }

        public DateTimeOffset NextRequestDispatchAt { get; set; }

        public DateTimeOffset RequestStartedAt { get; set; }

        public DateTimeOffset LastMatchingListingAt { get; set; }

        public byte StartCurrentRequestId { get; set; }

        public byte StartNextRequestId { get; set; }

        public byte ObservedCurrentRequestId { get; set; }

        public byte ObservedNextRequestId { get; set; }

        public uint PreviousListingCount { get; set; }

        public bool ReceivedMatchingListings { get; set; }

        public int MatchingOfferingEvents { get; set; }

        public List<MarketListingSnapshot> PendingListings { get; } = [];

        public HashSet<ListingIdentity> SeenListingIdentities { get; } = [];

        public bool SawWaitingForListings { get; set; }

        public bool SawRequestIdChange { get; set; }

        public bool SawListingCountChange { get; set; }
    }

    private sealed record TargetItem(uint ItemId, string ItemName);

    private sealed record FailedItem(uint ItemId, string ItemName, string Reason);

    private readonly record struct ListingIdentity(
        ulong ListingId,
        ulong SellingRetainerContentId,
        uint UnitPrice,
        uint Quantity,
        bool IsHq,
        byte TownId,
        uint TotalTax);
}
