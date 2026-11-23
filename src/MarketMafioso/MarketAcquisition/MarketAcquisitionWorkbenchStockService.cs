using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// One tickable source under a character in the "Count stock from" tree: its bags, one retainer, or (when
/// the retainers are not listed individually) all of its retainers. Unavailable sources are selected but no
/// longer observed; they count nothing and can only be unticked.
/// </summary>
public sealed record WorkbenchStockScopeSource(
    MarketAcquisitionStockSourceKey Key,
    string Label,
    bool Selected,
    bool Fixed,
    bool Available,
    DateTimeOffset? ObservedAtUtc);

/// <summary>One character in the "Count stock from" tree with its sources in game order.</summary>
public sealed record WorkbenchStockScopeCharacter(
    MarketAcquisitionStockSourceKey Character,
    bool IsCurrent,
    bool OtherAccount,
    bool Available,
    WorkbenchStockScopeSource? Bags,
    ImmutableArray<WorkbenchStockScopeSource> Retainers,
    MarketAcquisitionRetainerScope RetainerScope,
    ImmutableArray<ulong> KnownRetainerIds)
{
    public IEnumerable<WorkbenchStockScopeSource> Sources =>
        Bags is null ? Retainers : Retainers.Prepend(Bags);
}

/// <summary>One hover row under On hand. Quantity is null when that source is unknown.</summary>
public sealed record WorkbenchStockBreakdownRow(string Label, uint? Quantity, TimeSpan? StaleAge, TimeSpan? NewestAge = null);

/// <summary>
/// A target line's counted stock. Total is null when any counted source is unknown; Counted holds the
/// non-zero per-source contributions recorded on the line at finalize.
/// </summary>
public sealed record WorkbenchLineStock(
    uint? Total,
    TimeSpan? StaleAge,
    ImmutableArray<WorkbenchStockBreakdownRow> Rows,
    ImmutableArray<MarketAcquisitionCountedStockSource> Counted);

public sealed record WorkbenchStockView(
    bool HasStockSources,
    bool HasReserveLevels,
    bool HasItemHomes,
    ImmutableArray<WorkbenchStockScopeCharacter> Scope,
    ImmutableDictionary<uint, WorkbenchLineStock> Lines,
    QuartermasterReserveItems? Reserves,
    long Version,
    bool HasRemoteDelivery = false)
{
    public static WorkbenchStockView Empty { get; } = new(false, false, false, [], ImmutableDictionary<uint, WorkbenchLineStock>.Empty, null, 0);
}

/// <summary>
/// Framework-ticked cache behind the Workbench's stock columns. All Quartermaster IPC happens in
/// <see cref="Tick"/> (or in an inventory observation that needs fresher extra-source stock); the
/// panel only reads <see cref="View"/> and queues edits, so Draw never calls IPC.
/// </summary>
public sealed class MarketAcquisitionWorkbenchStockService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VisibleWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObservationMaxAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingEditLifetime = TimeSpan.FromSeconds(6);
    /// <summary>Off-screen refresh cadence while the buy list counts stock, so finalize never reads an empty cache.</summary>
    private static readonly TimeSpan OffscreenRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly QuartermasterIpcClient quartermaster;
    private readonly QuartermasterWorkbenchClient workbench;
    private readonly RetainerAwareMarketAcquisitionInventoryObserver observer;
    private readonly Func<QuartermasterOwnerScope> currentOwner;
    private readonly Func<MarketAcquisitionRequestDocument?> currentDocument;
    private readonly Action<string> warn;
    private readonly object gate = new();
    private readonly List<Action> pendingWrites = [];
    private readonly Dictionary<(uint ItemId, string Quality), (int Level, DateTimeOffset Until)> pendingLevels = new();
    private QuartermasterStockSources? sources;
    private QuartermasterReserveItems? reserves;
    private bool hasStockSources, hasReserveLevels, hasItemHomes, hasRemoteDelivery;
    private DateTimeOffset fetchedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastVisibleUtc = DateTimeOffset.MinValue;
    private bool dirty = true, refreshing, disposed;
    private long version;
    private int documentShape;

    public MarketAcquisitionWorkbenchStockService(
        QuartermasterIpcClient quartermaster,
        QuartermasterWorkbenchClient workbench,
        RetainerAwareMarketAcquisitionInventoryObserver observer,
        Func<QuartermasterOwnerScope> currentOwner,
        Func<MarketAcquisitionRequestDocument?> currentDocument,
        Action<string> warn)
    {
        this.quartermaster = quartermaster;
        this.workbench = workbench;
        this.observer = observer;
        this.currentOwner = currentOwner;
        this.currentDocument = currentDocument;
        this.warn = warn;
        quartermaster.Changed += OnQuartermasterChanged;
        observer.AdditionalStock = ObserveAdditionalStock;
        observer.OwnRetainerScope = ObserveOwnRetainerScope;
    }

    public WorkbenchStockView View { get; private set; } = WorkbenchStockView.Empty;

    /// <summary>Called from Draw: keeps the cache warm while the Workbench is on screen.</summary>
    public void MarkVisible() => lastVisibleUtc = DateTimeOffset.UtcNow;

    public void Tick()
    {
        if (disposed)
            return;
        FlushWrites();
        var now = DateTimeOffset.UtcNow;
        var document = TryDocument();
        var shape = DocumentShape(document);
        var reshaped = shape != documentShape;
        if (reshaped)
        {
            documentShape = shape;
            dirty = true;
        }
        if (now - lastVisibleUtc > VisibleWindow)
        {
            // Off screen the cache still follows a buy list that counts stock (finalize reads it): at once when
            // the list changes, otherwise on a slower cadence.
            if (document is null || !document.Lines.Any(line => CountsOnHand(document, line)) ||
                !reshaped && now - fetchedAtUtc < OffscreenRefreshInterval)
                return;
            Refresh(now);
            return;
        }
        if (!dirty && now - fetchedAtUtc < RefreshInterval)
            return;
        Refresh(now);
    }

    private static int DocumentShape(MarketAcquisitionRequestDocument? document)
    {
        if (document is null)
            return 0;
        var hash = new HashCode();
        hash.Add(document.CountRetainerStock);
        hash.Add(document.ExactAcquisitionAuthority is null);
        foreach (var line in document.Lines)
        {
            hash.Add(line.ItemId);
            hash.Add(line.HqPolicy);
            hash.Add(line.QuantityMode);
            hash.Add(line.TargetBasis);
        }
        foreach (var source in document.AdditionalStockSources)
        {
            hash.Add(source.LocalContentId);
            hash.Add(source.HomeWorldId);
            hash.Add(source.Container);
            hash.Add(source.RetainerId);
        }
        return hash.ToHashCode();
    }

    // ---- Edits (queued from Draw, sent on the next tick) ----------------------------------------

    /// <summary>Opens Quartermaster's placement editor for these lines' items; sent on the next tick.</summary>
    public void RequestOpenPlacement(IReadOnlyList<MarketAcquisitionRequestLineDocument> lines)
    {
        var items = lines.Where(line => line.ItemId > 0)
            .Select(line => (line.ItemId, line.ItemName, Quality: QualityFor(line.ItemId, line.HqPolicy)))
            .Distinct()
            .ToArray();
        if (items.Length == 0)
            return;
        lock (gate)
            pendingWrites.Add(() =>
            {
                var current = Reserves();
                string error = "Quartermaster reserve items are unavailable.";
                if (current is not null &&
                    workbench.TryOpenPlacement(current.ProviderInstanceId, currentOwner(), items, out _, out error))
                    return;
                warn($"[MarketMafioso] Could not open Quartermaster placement: {error}");
            });
    }

    public void RequestReserveLevel(MarketAcquisitionRequestLineDocument line, uint level)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        var units = (int)Math.Min(level, 999_999u);
        lock (gate)
        {
            pendingLevels[(line.ItemId, quality)] = (units, DateTimeOffset.UtcNow + PendingEditLifetime);
            pendingWrites.Add(() =>
            {
                var current = Reserves();
                string error = "Quartermaster reserve items are unavailable.";
                if (current is not null &&
                    workbench.TrySetReserveLevel(current.ProviderInstanceId, currentOwner(), line.ItemId, line.ItemName, quality, units, out _, out error))
                    return;
                lock (gate) pendingLevels.Remove((line.ItemId, quality));
                warn($"[MarketMafioso] Could not set the Quartermaster reserve level for {line.ItemName}: {error}");
            });
        }
    }

    private QuartermasterReserveItems? Reserves()
    {
        lock (gate)
            return reserves;
    }

    private void FlushWrites()
    {
        Action[] writes;
        lock (gate)
        {
            if (pendingWrites.Count == 0)
                return;
            writes = pendingWrites.ToArray();
            pendingWrites.Clear();
            dirty = true;
        }
        foreach (var write in writes)
        {
            try { write(); }
            catch (Exception ex) { warn($"[MarketMafioso] Quartermaster edit failed: {ex.Message}"); }
        }
    }

    // ---- Reads for the panel (in-memory only) ---------------------------------------------------

    /// <summary>The Quartermaster reserve rule a line maps to: exact quality, or for Either NQ first, then HQ.</summary>
    public QuartermasterReserveItem? FindReserve(uint itemId, string hqPolicy)
    {
        var items = View.Reserves?.Items ?? [];
        return hqPolicy switch
        {
            "HQOnly" => items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == "HqOnly"),
            "NQOnly" => items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == "NqOnly"),
            _ => items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == "NqOnly") ??
                 items.FirstOrDefault(item => item.ItemId == itemId && item.Quality == "HqOnly"),
        };
    }

    public string QualityFor(uint itemId, string hqPolicy) =>
        hqPolicy == "HQOnly" ? "HqOnly" : hqPolicy == "NQOnly" ? "NqOnly" : FindReserve(itemId, hqPolicy)?.Quality ?? "NqOnly";

    /// <summary>Reserve level for a line, preferring an edit still on its way to Quartermaster.</summary>
    public uint? ReserveLevelFor(MarketAcquisitionRequestLineDocument line)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        lock (gate)
            if (pendingLevels.TryGetValue((line.ItemId, quality), out var pending) && pending.Until > DateTimeOffset.UtcNow)
                return (uint)pending.Level;
        return FindReserve(line.ItemId, line.HqPolicy) is { ReserveUnits: > 0 } reserve ? (uint)reserve.ReserveUnits : null;
    }

    public bool HasPendingReserveLevel(MarketAcquisitionRequestLineDocument line)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        lock (gate)
            return pendingLevels.TryGetValue((line.ItemId, quality), out var pending) && pending.Until > DateTimeOffset.UtcNow;
    }

    /// <summary>A line's placement in Quartermaster: ranked home retainers, then Any space (neither = stays in bags).</summary>
    public (ImmutableArray<ulong> Homes, bool AnySpace) HomesFor(MarketAcquisitionRequestLineDocument line)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        var reserve = View.Reserves?.Items.FirstOrDefault(item => item.ItemId == line.ItemId && item.Quality == quality);
        return reserve is null ? ([], false) : (reserve.Homes, reserve.AnySpace);
    }

    /// <summary>The line's ranked homes on another character, or null (reserveRemoteHomes.v1).</summary>
    public QuartermasterRemoteHomes? RemoteHomesFor(MarketAcquisitionRequestLineDocument line)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        return View.Reserves?.Items.FirstOrDefault(item => item.ItemId == line.ItemId && item.Quality == quality)?.RemoteHomes;
    }

    /// <summary>
    /// Quartermaster's wording for a placement: "Eris → Skrrt → Any space; Dredgen Hope · Gringle → Scrongle", or
    /// "Stays in bags".
    /// </summary>
    public string PlacementSummary(MarketAcquisitionRequestLineDocument line)
    {
        var (homes, anySpace) = HomesFor(line);
        var names = homes.Distinct()
            .Select(id => View.Reserves?.Retainers.FirstOrDefault(retainer => retainer.RetainerId == id)?.RetainerName is { Length: > 0 } name
                ? name
                : $"Retainer {id}")
            .ToList();
        if (anySpace)
            names.Add("Any space");
        var local = names.Count == 0 ? null : string.Join(" → ", names);
        var remote = RemoteHomesFor(line) is { } other
            ? $"{other.CharacterName} · {string.Join(" → ", other.Retainers.Select(home => home.RetainerName ?? $"Retainer {home.RetainerId}"))}"
            : null;
        return local is null && remote is null ? "Stays in bags" : string.Join("; ", new[] { local, remote }.Where(part => part is not null));
    }

    /// <summary>
    /// Lines a delivered stow puts into retainers. A line homed only on another character counts only while stow is on
    /// and Quartermaster can forward it there; its Goes-to cell names the home either way.
    /// </summary>
    public int StowToRetainersCount(MarketAcquisitionRequestDocument document) =>
        !View.HasItemHomes || !document.StowAfterDelivery ? 0 : document.Lines.Count(line =>
            HomesFor(line) is var home && (home.Homes.Length > 0 || home.AnySpace) ||
            View.HasRemoteDelivery && RemoteHomesFor(line) is not null);

    /// <summary>Reserve items below their reserve level that the buy list does not already hold.</summary>
    public ImmutableArray<QuartermasterReserveItem> ReserveShortfalls(MarketAcquisitionRequestDocument document) =>
        !View.HasReserveLevels || View.Reserves is not { } current
            ? []
            : current.Items
                .Where(item => item.ReserveUnits > 0 && item.CountedUnits is { } counted && counted < item.ReserveUnits &&
                               document.Lines.All(line => line.ItemId != item.ItemId))
                .GroupBy(item => item.ItemId)
                .Select(group => group.First())
                .ToImmutableArray();

    // ---- Finalize --------------------------------------------------------------------------------

    /// <summary>
    /// The stock each selected source contributed to a target line at finalize (non-zero only). Reads the
    /// cached view built on the framework thread, so it is safe from a plan-preparation continuation.
    /// </summary>
    public IReadOnlyList<MarketAcquisitionCountedStockSource> CountedStockFor(MarketAcquisitionRequestLineDocument line) =>
        View.Lines.TryGetValue(line.ItemId, out var stock) && line.QuantityMode == "TargetQuantity" &&
        MarketAcquisitionTargetBases.CountsOnHand(line.TargetBasis)
            ? stock.Counted
            : [];

    // ---- Observer hook --------------------------------------------------------------------------

    /// <summary>
    /// Stock on the selected other characters' sources, added to on-hand observations of items on the
    /// Workbench. Null when nothing extra applies (no selection, item not on the list, or Quartermaster
    /// cannot report stock sources). A selected source with no observation at all drops out of the count
    /// (it is not offered either); a stale one still counts.
    /// </summary>
    private MarketAcquisitionInventoryObservation? ObserveAdditionalStock(uint itemId, string hqPolicy)
    {
        var document = TryDocument();
        if (document is null || document.AdditionalStockSources.Count == 0 || document.Lines.All(line => line.ItemId != itemId))
            return null;
        var now = DateTimeOffset.UtcNow;
        bool covered;
        lock (gate)
            covered = sources is { } cached && cached.ItemIds.Contains(itemId);
        if (!refreshing && (!covered || now - fetchedAtUtc > ObservationMaxAge))
            Refresh(now);
        if (!hasStockSources)
            return null;
        var owner = currentOwner();
        var quantity = 0u;
        foreach (var extra in SelectedExtras(document, owner))
        {
            if (extra.Bags && BagsContribution(extra.Character, itemId, hqPolicy) is { } bags)
                quantity = checked(quantity + bags.Quantity);
            if (RetainersContribution(extra.Character, extra.Retainers, itemId, hqPolicy) is { } retainers)
                quantity = checked(quantity + retainers.Quantity);
        }
        return MarketAcquisitionInventoryObservation.Available(quantity);
    }

    /// <summary>The current character's retainers ticked on the Workbench; every one when no document is loaded.</summary>
    private MarketAcquisitionRetainerScope ObserveOwnRetainerScope()
    {
        if (TryDocument() is not { } document)
            return MarketAcquisitionRetainerScope.Every;
        var owner = currentOwner();
        return MarketAcquisitionStockScope.OwnRetainers(document, owner.LocalContentId ?? 0, owner.HomeWorldId ?? 0);
    }

    // ---- Refresh ------------------------------------------------------------------------------

    private void OnQuartermasterChanged(QuartermasterChanged change) => dirty = true;

    private void Refresh(DateTimeOffset now)
    {
        if (refreshing)
            return;
        refreshing = true;
        try
        {
            dirty = false;
            var document = TryDocument();
            var itemIds = (document?.Lines ?? []).Select(line => line.ItemId).Where(id => id > 0).Distinct().Order().ToArray();
            var capabilities = quartermaster.TryGetCapabilities(out var advertised, out _) && advertised is not null
                ? advertised.Capabilities
                : [];
            var stock = capabilities.Contains(QuartermasterWorkbenchClient.StockSourcesCapability);
            var reserveItems = capabilities.Contains(QuartermasterWorkbenchClient.ReserveItemsCapability);
            var levels = reserveItems && capabilities.Contains(QuartermasterWorkbenchClient.ReserveLevelsCapability);
            var homes = reserveItems && capabilities.Contains(QuartermasterWorkbenchClient.PlacementEditorCapability);
            // Homes on another character are reached only by a delivered stow's forward (which direct delivery also needs).
            var remoteDelivery = capabilities.Contains(QuartermasterIpcClient.DeliveredStowCapability) &&
                                 capabilities.Contains(QuartermasterIpcClient.DeliveredStowForwardCapability);

            QuartermasterStockSources? fetchedSources = null;
            if (stock && !workbench.TryGetStockSources(itemIds, out fetchedSources, out var stockError))
                LogOnce(stockError);
            QuartermasterReserveItems? fetchedReserves = null;
            if (reserveItems)
            {
                // Reserve rules belong to one character; another character's homes and levels never show here.
                var owner = currentOwner();
                if (!workbench.TryGetReserveItems(out var reserveCandidate, out var reserveError))
                    LogOnce(reserveError);
                else if (owner.IsAvailable && IsOwner(reserveCandidate!.Owner.LocalContentId ?? 0, reserveCandidate.Owner.HomeWorldId ?? 0, owner))
                    fetchedReserves = reserveCandidate;
            }

            lock (gate)
            {
                sources = fetchedSources;
                reserves = fetchedReserves;
                hasStockSources = stock && fetchedSources is not null;
                hasReserveLevels = levels && fetchedReserves is not null;
                hasItemHomes = homes && fetchedReserves is not null;
                hasRemoteDelivery = remoteDelivery;
                fetchedAtUtc = now;
                ExpirePending(fetchedReserves, now);
            }

            View = BuildView(document);
        }
        catch (Exception ex)
        {
            LogOnce($"Workbench stock refresh failed: {ex.Message}");
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ExpirePending(QuartermasterReserveItems? current, DateTimeOffset now)
    {
        foreach (var key in pendingLevels.Where(pair => pair.Value.Until <= now ||
                     current?.Items.FirstOrDefault(item => item.ItemId == pair.Key.ItemId && item.Quality == pair.Key.Quality) is var item &&
                     (item?.ReserveUnits ?? 0) == pair.Value.Level).Select(pair => pair.Key).ToArray())
            pendingLevels.Remove(key);
    }

    private string? lastLogged;

    private void LogOnce(string message)
    {
        if (string.Equals(lastLogged, message, StringComparison.Ordinal))
            return;
        lastLogged = message;
        warn($"[MarketMafioso] {message}");
    }

    private WorkbenchStockView BuildView(MarketAcquisitionRequestDocument? document)
    {
        var owner = currentOwner();
        var snapshotRetainers = quartermaster.TryGetSnapshot(out var snapshot, out _) && snapshot is not null &&
                                owner.Matches(snapshot.Owner) && !snapshot.Retainers.IsDefaultOrEmpty
            ? snapshot
            : null;
        var scope = document is null ? [] : BuildScope(document, owner, snapshotRetainers);
        var lines = ImmutableDictionary.CreateBuilder<uint, WorkbenchLineStock>();
        foreach (var line in document?.Lines ?? [])
            if (CountsOnHand(document!, line) && !lines.ContainsKey(line.ItemId))
                lines[line.ItemId] = BuildLineStock(document!, line, owner, snapshotRetainers);
        QuartermasterReserveItems? currentReserves;
        bool stock, levels, homes, remote;
        lock (gate)
        {
            currentReserves = reserves;
            stock = hasStockSources;
            levels = hasReserveLevels;
            homes = hasItemHomes;
            remote = hasRemoteDelivery;
        }
        return new WorkbenchStockView(stock, levels, homes, scope, lines.ToImmutable(), currentReserves, ++version, remote);
    }

    /// <summary>
    /// The "Count stock from" tree: the current character, the other characters of this account by name, then
    /// characters on other accounts. Only observed sources are offered (a never-observed one would be unknown,
    /// never zero); a selected source that is no longer observed stays listed, unavailable, so it can be unticked.
    /// </summary>
    private ImmutableArray<WorkbenchStockScopeCharacter> BuildScope(
        MarketAcquisitionRequestDocument document,
        QuartermasterOwnerScope owner,
        QuartermasterSnapshot? snapshot)
    {
        QuartermasterStockSources? current;
        bool stock;
        lock (gate)
        {
            current = sources;
            stock = hasStockSources;
        }
        var known = stock && current is not null ? current.Characters : [];
        var result = ImmutableArray.CreateBuilder<WorkbenchStockScopeCharacter>();
        var ownerId = owner.LocalContentId ?? 0;
        var ownerWorld = owner.HomeWorldId ?? 0;
        var ownerName = string.IsNullOrWhiteSpace(owner.CharacterName) ? "This character" : owner.CharacterName!;
        var ownerKey = new MarketAcquisitionStockSourceKey(ownerId, ownerWorld, ownerName, owner.HomeWorldName ?? string.Empty,
            MarketAcquisitionStockSourceKey.Bags);

        // The current character: bags always count; retainers from the owner-matched snapshot, else Quartermaster's list.
        var ownScope = MarketAcquisitionStockScope.OwnRetainers(document, ownerId, ownerWorld);
        var ownListed = snapshot is not null
            ? snapshot.Retainers.Select(retainer => (retainer.RetainerId, retainer.RetainerName,
                ObservedAtUtc: (DateTimeOffset?)(retainer.Bags.Select(bag => bag.ObservedAtUtc).Where(at => at is not null).Min()
                                                 ?? retainer.ObservedAtUtc))).ToList()
            : known.FirstOrDefault(character => IsOwner(character.LocalContentId, character.HomeWorldId, owner))?.Retainers is { IsDefaultOrEmpty: false } ownerListed
                ? ownerListed.Select(retainer => (retainer.RetainerId, retainer.RetainerName, ObservedAtUtc: (DateTimeOffset?)retainer.ObservedAtUtc)).ToList()
                : [];
        var ownRetainers = RetainerNodes(ownerKey, ownListed, ownScope, document.CountRetainerStock);
        result.Add(new WorkbenchStockScopeCharacter(
            ownerKey, true, false, true,
            new WorkbenchStockScopeSource(ownerKey, "Bags", true, true, true, null),
            ownRetainers, ownScope, ownListed.Select(retainer => retainer.RetainerId).ToImmutableArray()));

        var others = known
            .Where(character => !IsOwner(character.LocalContentId, character.HomeWorldId, owner))
            .OrderBy(character => character.OtherAccount)
            .ThenBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var character in others)
        {
            var key = new MarketAcquisitionStockSourceKey(character.LocalContentId, character.HomeWorldId, character.CharacterName,
                character.HomeWorldName, MarketAcquisitionStockSourceKey.Bags);
            var bagsSelected = MarketAcquisitionStockScope.CharacterBags(document, character.LocalContentId, character.HomeWorldId);
            var bags = character.Bags is { } observedBags
                ? new WorkbenchStockScopeSource(key, "Bags", bagsSelected, false, true, observedBags.ObservedAtUtc)
                : bagsSelected
                    ? new WorkbenchStockScopeSource(key, "Bags", true, false, false, null)
                    : null;
            var scope = MarketAcquisitionStockScope.CharacterRetainers(document, character.LocalContentId, character.HomeWorldId);
            var listed = character.Retainers is { IsDefaultOrEmpty: false } observed
                ? observed.Select(retainer => (retainer.RetainerId, retainer.RetainerName, ObservedAtUtc: (DateTimeOffset?)retainer.ObservedAtUtc)).ToList()
                : [];
            var retainers = RetainerNodes(key, listed, scope, !scope.IsNone);
            if (bags is null && retainers.IsEmpty)
                continue;
            result.Add(new WorkbenchStockScopeCharacter(key, false, character.OtherAccount, true, bags, retainers, scope,
                listed.Select(retainer => retainer.RetainerId).ToImmutableArray()));
        }

        // Selected characters Quartermaster no longer reports: listed so their scope can still be cleared.
        foreach (var group in document.AdditionalStockSources
                     .Where(key => !IsOwner(key.LocalContentId, key.HomeWorldId, owner) &&
                                   !result.Any(character => character.Character.IsCharacter(key.LocalContentId, key.HomeWorldId)))
                     .GroupBy(key => (key.LocalContentId, key.HomeWorldId)))
        {
            var first = group.First();
            var key = first with { Container = MarketAcquisitionStockSourceKey.Bags, RetainerId = 0, RetainerName = string.Empty };
            var bags = group.Any(source => source.Container == MarketAcquisitionStockSourceKey.Bags)
                ? new WorkbenchStockScopeSource(key, "Bags", true, false, false, null)
                : null;
            var scope = MarketAcquisitionStockScope.CharacterRetainers(group, key.LocalContentId, key.HomeWorldId);
            var retainers = RetainerNodes(key, [], scope, !scope.IsNone, group.ToList());
            result.Add(new WorkbenchStockScopeCharacter(key, false, false, false, bags, retainers, scope, []));
        }
        return result.ToImmutable();
    }

    /// <summary>One node per listed retainer, then any selected retainer no longer listed.</summary>
    private static ImmutableArray<WorkbenchStockScopeSource> RetainerNodes(
        MarketAcquisitionStockSourceKey character,
        IReadOnlyList<(ulong RetainerId, string RetainerName, DateTimeOffset? ObservedAtUtc)> listed,
        MarketAcquisitionRetainerScope scope,
        bool showWhole,
        IReadOnlyList<MarketAcquisitionStockSourceKey>? selectedKeys = null)
    {
        var nodes = ImmutableArray.CreateBuilder<WorkbenchStockScopeSource>();
        foreach (var retainer in listed)
            nodes.Add(new(character.ForRetainer(retainer.RetainerId, retainer.RetainerName),
                string.IsNullOrWhiteSpace(retainer.RetainerName) ? $"Retainer {retainer.RetainerId}" : retainer.RetainerName,
                scope.Includes(retainer.RetainerId), false, true, retainer.ObservedAtUtc));
        foreach (var id in scope.Ids.Where(id => listed.All(retainer => retainer.RetainerId != id)).Order())
        {
            var name = selectedKeys?.FirstOrDefault(key => key.RetainerId == id)?.RetainerName;
            nodes.Add(new(character.ForRetainer(id, name ?? string.Empty),
                string.IsNullOrWhiteSpace(name) ? $"Retainer {id}" : name, true, false, false, null));
        }
        // Retainers that cannot be listed cannot be counted either: offered only so the choice can be undone.
        if (listed.Count == 0 && scope.All && showWhole)
            nodes.Add(new(character with { Container = MarketAcquisitionStockSourceKey.Retainers }, "Retainers", true, false, false, null));
        return nodes.ToImmutable();
    }

    private WorkbenchLineStock BuildLineStock(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestLineDocument line,
        QuartermasterOwnerScope owner,
        QuartermasterSnapshot? snapshot)
    {
        var rows = ImmutableArray.CreateBuilder<WorkbenchStockBreakdownRow>();
        var counted = ImmutableArray.CreateBuilder<MarketAcquisitionCountedStockSource>();
        var ownerName = string.IsNullOrWhiteSpace(owner.CharacterName) ? "This character" : owner.CharacterName!;
        var ownerWorld = owner.HomeWorldName ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var carried = observer.Observe(line.ItemId, line.HqPolicy);
        rows.Add(new(SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Bags), carried.IsAvailable ? carried.Quantity : null, null));
        if (carried is { IsAvailable: true, Quantity: > 0 })
            counted.Add(new(owner.LocalContentId ?? 0, owner.HomeWorldId ?? 0, ownerName, ownerWorld,
                MarketAcquisitionStockSourceKey.Bags, carried.Quantity, now));
        if (document.CountRetainerStock)
        {
            var scope = MarketAcquisitionStockScope.OwnRetainers(document, owner.LocalContentId ?? 0, owner.HomeWorldId ?? 0);
            if (PrimaryRetainers(snapshot, scope, line.ItemId, line.HqPolicy) is not { } retainers)
                rows.Add(new(SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Retainers), null, null));
            else
            {
                AddRetainerRows(rows, ownerName, retainers.Select(retainer => (retainer.Name, retainer.Quantity, retainer.ObservedAtUtc)).ToList());
                if (retainers.Any(retainer => retainer.Quantity > 0))
                    counted.Add(new(owner.LocalContentId ?? 0, owner.HomeWorldId ?? 0, ownerName, ownerWorld,
                        MarketAcquisitionStockSourceKey.Retainers,
                        retainers.Aggregate(0u, (sum, retainer) => checked(sum + retainer.Quantity)),
                        retainers.Min(retainer => retainer.ObservedAtUtc)));
            }
        }

        bool stock;
        lock (gate)
            stock = hasStockSources;
        if (stock)
            foreach (var extra in SelectedExtras(document, owner))
            {
                // Never observed: dropped from the count quietly, not shown as unknown or zero.
                var name = extra.Character?.CharacterName ?? extra.Key.CharacterName;
                if (extra.Bags && BagsContribution(extra.Character, line.ItemId, line.HqPolicy) is { } bags)
                {
                    if (bags.Quantity > 0)
                        counted.Add(new(extra.Key.LocalContentId, extra.Key.HomeWorldId, name, extra.Character!.HomeWorldName,
                            MarketAcquisitionStockSourceKey.Bags, bags.Quantity, bags.ObservedAtUtc, bags.HqQuantity));
                    rows.Add(new(SourceLabel(name, MarketAcquisitionStockSourceKey.Bags), bags.Quantity, StaleAge(bags.ObservedAtUtc)));
                }
                if (RetainersContribution(extra.Character, extra.Retainers, line.ItemId, line.HqPolicy) is not { } retainers)
                    continue;
                if (retainers.Quantity > 0)
                    counted.Add(new(extra.Key.LocalContentId, extra.Key.HomeWorldId, name, extra.Character!.HomeWorldName,
                        MarketAcquisitionStockSourceKey.Retainers, retainers.Quantity, retainers.ObservedAtUtc, retainers.HqQuantity));
                AddRetainerRows(rows, name, retainers.PerRetainer);
            }

        var total = observer.Observe(line.ItemId, line.HqPolicy, line.TargetBasis);
        // Only sources that hold the item age the line: a retainer nobody has opened lately says nothing
        // about stock counted from the others.
        var stale = rows.Where(row => row is { StaleAge: not null, Quantity: > 0 })
            .Select(row => row.StaleAge!.Value).DefaultIfEmpty().Max();
        return new WorkbenchLineStock(total.IsAvailable ? total.Quantity : null, stale > TimeSpan.Zero ? stale : null,
            rows.ToImmutable(), counted.ToImmutable());
    }

    /// <summary>
    /// Hover rows for one character's counted retainers: each retainer holding the item, or one zero row when none
    /// does (a group seen at different times reads as an age range).
    /// </summary>
    private static void AddRetainerRows(
        ImmutableArray<WorkbenchStockBreakdownRow>.Builder rows,
        string characterName,
        IReadOnlyList<(string Name, uint Quantity, DateTimeOffset ObservedAtUtc)> retainers)
    {
        if (retainers.Any(retainer => retainer.Quantity > 0))
        {
            foreach (var retainer in retainers.Where(retainer => retainer.Quantity > 0))
                rows.Add(new($"{characterName} · {retainer.Name}", retainer.Quantity, StaleAge(retainer.ObservedAtUtc)));
            return;
        }
        if (retainers.Count == 1)
        {
            rows.Add(new($"{characterName} · {retainers[0].Name}", 0, StaleAge(retainers[0].ObservedAtUtc)));
            return;
        }
        rows.Add(new(SourceLabel(characterName, MarketAcquisitionStockSourceKey.Retainers), 0,
            StaleAge(retainers.Count == 0 ? null : retainers.Min(retainer => retainer.ObservedAtUtc)),
            Age(retainers.Count == 0 ? null : retainers.Max(retainer => retainer.ObservedAtUtc))));
    }

    // ---- Stock helpers --------------------------------------------------------------------------

    /// <summary>Another character's selected bags and retainers.</summary>
    private sealed record SelectedCharacter(
        MarketAcquisitionStockSourceKey Key,
        QuartermasterStockCharacter? Character,
        bool Bags,
        MarketAcquisitionRetainerScope Retainers);

    private IEnumerable<SelectedCharacter> SelectedExtras(
        MarketAcquisitionRequestDocument document,
        QuartermasterOwnerScope owner)
    {
        QuartermasterStockSources? current;
        lock (gate)
            current = sources;
        foreach (var group in document.AdditionalStockSources
                     .Where(key => !IsOwner(key.LocalContentId, key.HomeWorldId, owner))
                     .GroupBy(key => (key.LocalContentId, key.HomeWorldId)))
        {
            var key = group.First();
            yield return new SelectedCharacter(
                key,
                current?.Characters.FirstOrDefault(character => key.IsCharacter(character.LocalContentId, character.HomeWorldId)),
                group.Any(source => source.Container == MarketAcquisitionStockSourceKey.Bags),
                MarketAcquisitionStockScope.CharacterRetainers(group, key.LocalContentId, key.HomeWorldId));
        }
    }

    private static (uint Quantity, uint HqQuantity, DateTimeOffset ObservedAtUtc)? BagsContribution(
        QuartermasterStockCharacter? character,
        uint itemId,
        string hqPolicy) =>
        character?.Bags is { } bags
            ? (Sum(bags.Items, itemId, hqPolicy), Sum(bags.Items, itemId, hqPolicy, hqOnly: true), bags.ObservedAtUtc)
            : null;

    /// <summary>The selected retainers' stock, or null when none of them is observed.</summary>
    private static (uint Quantity, uint HqQuantity, DateTimeOffset ObservedAtUtc,
        IReadOnlyList<(string Name, uint Quantity, DateTimeOffset ObservedAtUtc)> PerRetainer)? RetainersContribution(
        QuartermasterStockCharacter? character,
        MarketAcquisitionRetainerScope scope,
        uint itemId,
        string hqPolicy)
    {
        if (scope.IsNone || character?.Retainers is not { IsDefaultOrEmpty: false } retainers)
            return null;
        var selected = retainers.Where(retainer => scope.Includes(retainer.RetainerId)).ToList();
        if (selected.Count == 0)
            return null;
        var quantity = 0u;
        var hq = 0u;
        var perRetainer = new List<(string, uint, DateTimeOffset)>(selected.Count);
        foreach (var retainer in selected)
        {
            var units = Sum(retainer.Items, itemId, hqPolicy);
            quantity = checked(quantity + units);
            hq = checked(hq + Sum(retainer.Items, itemId, hqPolicy, hqOnly: true));
            perRetainer.Add((string.IsNullOrWhiteSpace(retainer.RetainerName) ? $"Retainer {retainer.RetainerId}" : retainer.RetainerName,
                units, retainer.ObservedAtUtc));
        }
        return (quantity, hq, selected.Min(retainer => retainer.ObservedAtUtc), perRetainer);
    }

    private static uint Sum(ImmutableArray<QuartermasterStockItem> items, uint itemId, string hqPolicy, bool hqOnly = false) =>
        (uint)Math.Min(uint.MaxValue, items
            .Where(item => item.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, item.Hq) && (!hqOnly || item.Hq))
            .Sum(item => item.Quantity));

    /// <summary>
    /// The current character's counted retainers and their stock of the item, from the same owner-matched
    /// Quartermaster snapshot the inventory observer counts. Null when that snapshot cannot vouch for the item.
    /// </summary>
    private static List<(string Name, uint Quantity, DateTimeOffset ObservedAtUtc)>? PrimaryRetainers(
        QuartermasterSnapshot? snapshot,
        MarketAcquisitionRetainerScope scope,
        uint itemId,
        string hqPolicy)
    {
        if (snapshot is null || snapshot.Retainers.IsDefaultOrEmpty)
            return null;
        var counted = snapshot.Retainers.Where(retainer => scope.Includes(retainer.RetainerId)).ToList();
        if (counted.Any(retainer => !QuartermasterStockCoverage.CoversItem(retainer, itemId)))
            return null;
        var crystal = itemId is >= 2 and <= 19;
        var result = new List<(string, uint, DateTimeOffset)>();
        foreach (var retainer in counted)
        {
            var bags = retainer.Bags.Where(bag =>
                bag.BagName.Equals("RetainerCrystals", StringComparison.OrdinalIgnoreCase) == crystal).ToArray();
            if (bags.Length == 0)
                return null;
            var observed = bags.Select(bag => bag.ObservedAtUtc).Where(at => at is not null).Min() ?? snapshot.GeneratedAtUtc;
            var quantity = bags.SelectMany(bag => bag.Items)
                .Where(item => item.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, item.IsHq))
                .Aggregate(0u, (sum, item) => checked(sum + item.Quantity));
            result.Add((retainer.RetainerName, quantity, observed));
        }
        return result;
    }

    private static bool CountsOnHand(MarketAcquisitionRequestDocument document, MarketAcquisitionRequestLineDocument line) =>
        document.ExactAcquisitionAuthority is null &&
        line.QuantityMode == "TargetQuantity" &&
        MarketAcquisitionTargetBases.CountsOnHand(line.TargetBasis);

    private static bool IsOwner(ulong localContentId, uint homeWorldId, QuartermasterOwnerScope owner) =>
        owner.LocalContentId == localContentId && owner.HomeWorldId == homeWorldId;

    private static TimeSpan? StaleAge(DateTimeOffset? observedAtUtc) =>
        observedAtUtc is { } at && DateTimeOffset.UtcNow - at is var age &&
        age > RetainerAwareMarketAcquisitionInventoryObserver.RetainerStockStaleThreshold
            ? age
            : null;

    private static TimeSpan? Age(DateTimeOffset? observedAtUtc) =>
        observedAtUtc is { } at ? DateTimeOffset.UtcNow - at : null;

    public static string SourceLabel(string characterName, string container) => $"{characterName} · {container}";

    private MarketAcquisitionRequestDocument? TryDocument()
    {
        try { return currentDocument(); }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        quartermaster.Changed -= OnQuartermasterChanged;
        observer.AdditionalStock = null;
        observer.OwnRetainerScope = null;
    }
}
