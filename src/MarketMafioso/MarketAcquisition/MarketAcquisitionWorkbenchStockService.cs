using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>One choice under "Count stock from".</summary>
public sealed record WorkbenchStockSourceOption(
    MarketAcquisitionStockSourceKey Key,
    string Label,
    bool IsCurrentCharacter,
    bool Selected,
    bool Fixed,
    TimeSpan? StaleAge);

/// <summary>One hover row under On hand. Quantity is null when that source is unknown.</summary>
public sealed record WorkbenchStockBreakdownRow(string Label, uint? Quantity, TimeSpan? StaleAge);

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
    ImmutableArray<WorkbenchStockSourceOption> SourceOptions,
    ImmutableDictionary<uint, WorkbenchLineStock> Lines,
    QuartermasterReserveItems? Reserves,
    long Version)
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

    private readonly QuartermasterIpcClient quartermaster;
    private readonly QuartermasterWorkbenchClient workbench;
    private readonly RetainerAwareMarketAcquisitionInventoryObserver observer;
    private readonly Func<QuartermasterOwnerScope> currentOwner;
    private readonly Func<MarketAcquisitionRequestDocument?> currentDocument;
    private readonly Action<string> warn;
    private readonly object gate = new();
    private readonly List<Action> pendingWrites = [];
    private readonly Dictionary<(uint ItemId, string Quality), (ImmutableArray<ulong> Homes, DateTimeOffset Until)> pendingHomes = new();
    private readonly Dictionary<(uint ItemId, string Quality), (int Level, DateTimeOffset Until)> pendingLevels = new();
    private QuartermasterStockSources? sources;
    private QuartermasterReserveItems? reserves;
    private bool hasStockSources, hasReserveLevels, hasItemHomes;
    private DateTimeOffset fetchedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastVisibleUtc = DateTimeOffset.MinValue;
    private bool dirty = true, refreshing, disposed;
    private long version;

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
        if (now - lastVisibleUtc > VisibleWindow || !dirty && now - fetchedAtUtc < RefreshInterval)
            return;
        Refresh(now);
    }

    // ---- Edits (queued from Draw, sent on the next tick) ----------------------------------------

    public void RequestItemHome(MarketAcquisitionRequestLineDocument line, IReadOnlyList<ulong> homes)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        var target = homes.Distinct().ToImmutableArray();
        lock (gate)
        {
            pendingHomes[(line.ItemId, quality)] = (target, DateTimeOffset.UtcNow + PendingEditLifetime);
            pendingWrites.Add(() =>
            {
                var current = Reserves();
                string error = "Quartermaster reserve items are unavailable.";
                if (current is not null &&
                    workbench.TrySetItemHome(current.ProviderInstanceId, currentOwner(), line.ItemId, line.ItemName, quality, target, out _, out error))
                    return;
                lock (gate) pendingHomes.Remove((line.ItemId, quality));
                warn($"[MarketMafioso] Could not set the Quartermaster home for {line.ItemName}: {error}");
            });
        }
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

    /// <summary>Home retainers for a line (empty = stays in bags), preferring an edit still in flight.</summary>
    public (ImmutableArray<ulong> Homes, bool AnySpace) HomesFor(MarketAcquisitionRequestLineDocument line)
    {
        var quality = QualityFor(line.ItemId, line.HqPolicy);
        lock (gate)
            if (pendingHomes.TryGetValue((line.ItemId, quality), out var pending) && pending.Until > DateTimeOffset.UtcNow)
                return (pending.Homes, false);
        var reserve = View.Reserves?.Items.FirstOrDefault(item => item.ItemId == line.ItemId && item.Quality == quality);
        return reserve is null ? ([], false) : (reserve.Homes, reserve.AnySpace);
    }

    public int StowToRetainersCount(MarketAcquisitionRequestDocument document) =>
        !View.HasItemHomes ? 0 : document.Lines.Count(line => HomesFor(line) is var home && (home.Homes.Length > 0 || home.AnySpace));

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
    /// cannot report stock sources); unavailable when a selected source has never been observed.
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
        foreach (var (key, character) in SelectedExtras(document, owner))
        {
            if (Contribution(character, key.Container, itemId, hqPolicy) is not { } contribution)
                return MarketAcquisitionInventoryObservation.Unavailable(
                    $"{SourceLabel(key.CharacterName, key.Container)} stock has not been observed; stock is unknown, not zero.");
            quantity = checked(quantity + contribution.Quantity);
        }
        return MarketAcquisitionInventoryObservation.Available(quantity);
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
            var homes = reserveItems && capabilities.Contains(QuartermasterWorkbenchClient.ItemHomesCapability);

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
        foreach (var key in pendingHomes.Where(pair => pair.Value.Until <= now ||
                     current?.Items.FirstOrDefault(item => item.ItemId == pair.Key.ItemId && item.Quality == pair.Key.Quality) is var item &&
                     (item?.Homes ?? []).SequenceEqual(pair.Value.Homes)).Select(pair => pair.Key).ToArray())
            pendingHomes.Remove(key);
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
        var options = document is null ? [] : BuildSourceOptions(document, owner, snapshotRetainers);
        var lines = ImmutableDictionary.CreateBuilder<uint, WorkbenchLineStock>();
        foreach (var line in document?.Lines ?? [])
            if (CountsOnHand(document!, line) && !lines.ContainsKey(line.ItemId))
                lines[line.ItemId] = BuildLineStock(document!, line, owner, snapshotRetainers);
        QuartermasterReserveItems? currentReserves;
        bool stock, levels, homes;
        lock (gate)
        {
            currentReserves = reserves;
            stock = hasStockSources;
            levels = hasReserveLevels;
            homes = hasItemHomes;
        }
        return new WorkbenchStockView(stock, levels, homes, options, lines.ToImmutable(), currentReserves, ++version);
    }

    private ImmutableArray<WorkbenchStockSourceOption> BuildSourceOptions(
        MarketAcquisitionRequestDocument document,
        QuartermasterOwnerScope owner,
        QuartermasterSnapshot? snapshot)
    {
        var options = ImmutableArray.CreateBuilder<WorkbenchStockSourceOption>();
        var ownerName = string.IsNullOrWhiteSpace(owner.CharacterName) ? "This character" : owner.CharacterName!;
        var ownerId = owner.LocalContentId ?? 0;
        var ownerWorld = owner.HomeWorldId ?? 0;
        options.Add(new(new(ownerId, ownerWorld, ownerName, owner.HomeWorldName ?? string.Empty, MarketAcquisitionStockSourceKey.Bags),
            SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Bags), true, true, true, null));
        if (snapshot is not null || document.CountRetainerStock)
        {
            var oldest = snapshot?.Retainers.SelectMany(retainer => retainer.Bags)
                .Select(bag => bag.ObservedAtUtc).Where(at => at is not null).Min();
            options.Add(new(new(ownerId, ownerWorld, ownerName, owner.HomeWorldName ?? string.Empty, MarketAcquisitionStockSourceKey.Retainers),
                SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Retainers), true, document.CountRetainerStock, false,
                StaleAge(oldest ?? snapshot?.GeneratedAtUtc)));
        }

        QuartermasterStockSources? current;
        bool stock;
        lock (gate)
        {
            current = sources;
            stock = hasStockSources;
        }
        if (!stock || current is null)
            return options.ToImmutable();

        var others = current.Characters
            .Where(character => !IsOwner(character.LocalContentId, character.HomeWorldId, owner))
            .ToList();
        foreach (var character in others)
        {
            var key = (character.LocalContentId, character.HomeWorldId);
            bool Selected(string container) => document.AdditionalStockSources.Any(source =>
                source.IsCharacter(key.LocalContentId, key.HomeWorldId) && source.Container == container);
            if (character.Bags is not null || Selected(MarketAcquisitionStockSourceKey.Bags))
                options.Add(new(new(character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName,
                        MarketAcquisitionStockSourceKey.Bags),
                    SourceLabel(character.CharacterName, MarketAcquisitionStockSourceKey.Bags), false,
                    Selected(MarketAcquisitionStockSourceKey.Bags), false, StaleAge(character.Bags?.ObservedAtUtc)));
            if (character.Retainers is not null || Selected(MarketAcquisitionStockSourceKey.Retainers))
                options.Add(new(new(character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName,
                        MarketAcquisitionStockSourceKey.Retainers),
                    SourceLabel(character.CharacterName, MarketAcquisitionStockSourceKey.Retainers), false,
                    Selected(MarketAcquisitionStockSourceKey.Retainers), false,
                    StaleAge(character.Retainers is { IsDefaultOrEmpty: false } retainers ? retainers.Min(retainer => retainer.ObservedAtUtc) : null)));
        }

        // A saved choice stays visible (and removable) after its character drops out of Quartermaster.
        foreach (var missing in document.AdditionalStockSources.Where(source =>
                     !IsOwner(source.LocalContentId, source.HomeWorldId, owner) &&
                     others.All(character => !source.IsCharacter(character.LocalContentId, character.HomeWorldId))))
            options.Add(new(missing, SourceLabel(missing.CharacterName, missing.Container), false, true, false, null));
        return options.ToImmutable();
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
            if (PrimaryRetainers(snapshot, line.ItemId, line.HqPolicy) is not { } retainers)
                rows.Add(new(SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Retainers), null, null));
            else if (retainers.All(retainer => retainer.Quantity == 0))
                rows.Add(new(SourceLabel(ownerName, MarketAcquisitionStockSourceKey.Retainers), 0,
                    StaleAge(retainers.Count == 0 ? null : retainers.Min(retainer => retainer.ObservedAtUtc))));
            else
            {
                foreach (var retainer in retainers.Where(retainer => retainer.Quantity > 0))
                    rows.Add(new($"{ownerName} · {retainer.Name}", retainer.Quantity, StaleAge(retainer.ObservedAtUtc)));
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
            foreach (var (key, character) in SelectedExtras(document, owner))
            {
                var name = character?.CharacterName ?? key.CharacterName;
                if (Contribution(character, key.Container, line.ItemId, line.HqPolicy) is not { } contribution)
                {
                    rows.Add(new(SourceLabel(name, key.Container), null, null));
                    continue;
                }
                if (contribution.Quantity > 0)
                    counted.Add(new(key.LocalContentId, key.HomeWorldId, name, character!.HomeWorldName,
                        key.Container, contribution.Quantity, contribution.ObservedAtUtc));
                if (key.Container == MarketAcquisitionStockSourceKey.Retainers && contribution.Quantity > 0)
                {
                    foreach (var retainer in character!.Retainers!.Value)
                        if (Sum(retainer.Items, line.ItemId, line.HqPolicy) is var quantity && quantity > 0)
                            rows.Add(new($"{name} · {retainer.RetainerName}", quantity, StaleAge(retainer.ObservedAtUtc)));
                    continue;
                }
                rows.Add(new(SourceLabel(name, key.Container), contribution.Quantity, StaleAge(contribution.ObservedAtUtc)));
            }

        var total = observer.Observe(line.ItemId, line.HqPolicy, line.TargetBasis);
        var stale = rows.Where(row => row.StaleAge is not null).Select(row => row.StaleAge!.Value).DefaultIfEmpty().Max();
        return new WorkbenchLineStock(total.IsAvailable ? total.Quantity : null, stale > TimeSpan.Zero ? stale : null,
            rows.ToImmutable(), counted.ToImmutable());
    }

    // ---- Stock helpers --------------------------------------------------------------------------

    private IEnumerable<(MarketAcquisitionStockSourceKey Key, QuartermasterStockCharacter? Character)> SelectedExtras(
        MarketAcquisitionRequestDocument document,
        QuartermasterOwnerScope owner)
    {
        QuartermasterStockSources? current;
        lock (gate)
            current = sources;
        foreach (var key in document.AdditionalStockSources)
        {
            if (IsOwner(key.LocalContentId, key.HomeWorldId, owner))
                continue;
            yield return (key, current?.Characters.FirstOrDefault(character =>
                key.IsCharacter(character.LocalContentId, character.HomeWorldId)));
        }
    }

    private static (uint Quantity, DateTimeOffset ObservedAtUtc)? Contribution(
        QuartermasterStockCharacter? character,
        string container,
        uint itemId,
        string hqPolicy)
    {
        if (character is null)
            return null;
        if (container == MarketAcquisitionStockSourceKey.Bags)
            return character.Bags is { } bags ? (Sum(bags.Items, itemId, hqPolicy), bags.ObservedAtUtc) : null;
        if (character.Retainers is not { IsDefaultOrEmpty: false } retainers)
            return null;
        var quantity = 0u;
        foreach (var retainer in retainers)
            quantity = checked(quantity + Sum(retainer.Items, itemId, hqPolicy));
        return (quantity, retainers.Min(retainer => retainer.ObservedAtUtc));
    }

    private static uint Sum(ImmutableArray<QuartermasterStockItem> items, uint itemId, string hqPolicy) =>
        (uint)Math.Min(uint.MaxValue, items
            .Where(item => item.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, item.Hq))
            .Sum(item => item.Quantity));

    /// <summary>
    /// The current character's retainers holding the item, from the same owner-matched Quartermaster
    /// snapshot the inventory observer counts. Null when that snapshot cannot vouch for the item.
    /// </summary>
    private static List<(string Name, uint Quantity, DateTimeOffset ObservedAtUtc)>? PrimaryRetainers(
        QuartermasterSnapshot? snapshot,
        uint itemId,
        string hqPolicy)
    {
        if (snapshot is null || snapshot.Retainers.IsDefaultOrEmpty ||
            snapshot.Retainers.Any(retainer => !QuartermasterStockCoverage.CoversItem(retainer, itemId)))
            return null;
        var crystal = itemId is >= 2 and <= 19;
        var result = new List<(string, uint, DateTimeOffset)>();
        foreach (var retainer in snapshot.Retainers)
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
    }
}
