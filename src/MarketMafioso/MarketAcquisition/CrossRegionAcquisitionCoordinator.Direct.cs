using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// Direct delivery to homes on another character, used only when "Stow after delivery" is on and Quartermaster can
/// plan arrivals, deliver into a named retainer and forward a delivered stow.
///
/// Trip order: a courier transfer has one source and one destination, so a consignment goes straight to another
/// character R only when every unit of it is bound for R's homes. The plan fills the item's global home chain in rank
/// order across characters, each home up to its cap and room, so that happens when R ranks first or when every home
/// ranked before R's is full or at its cap. Then the pickup that would have gone to the primary goes to R instead, and
/// the later forward from the primary to R never happens. A consignment split between the primary and R, or across
/// several other characters, still goes to the primary in one trip; the delivered stow fills the primary's homes and
/// Quartermaster's forward carries every other character's share on in one courier run, which costs the same trades
/// as splitting the pickup would. Counted stock already on its home character is not collected at all.
///
/// Journaling: the route (destination and per-retainer units) is persisted with the leg's request id before the
/// courier sees it, so a replay sends the identical request. A direct delivery refused for its destination (before
/// anything moved) is recorded, logged and resent to the primary once under a new request id; it is never retried
/// towards R. Any other refusal or failure keeps the leg's usual handling and is never resent. Only a completed run
/// counts as delivered there.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    public const string CodeKeptAtHome = "KeptAtHome";
    public const string CodeDirectMixedConsignment = "DirectMixedConsignment";
    public const string CodeDirectPlanUnavailable = "DirectPlanUnavailable";
    public const string CodeDirectDestinationNotInRoster = "DirectDestinationNotInRoster";
    public const string CodeForwardNotObserved = "ForwardNotObserved";
    public const string CodeRetrievalWouldTakeHomeStock = "RetrievalWouldTakeHomeStock";
    public const string CodeForwardStillRunning = "ForwardStillRunning";
    private const string CodeCourierAlreadyRunning = "CourierAlreadyRunning";
    private const string CodeCourierRunCleared = "CourierRunCleared";
    private const string CodeRequestConflict = "RequestConflict";

    /// <summary>
    /// Quartermaster's planner refusals that concern only the destination's homes or reach. They are answered before
    /// anything moves, so a direct delivery refused for one of them goes to the primary instead.
    /// </summary>
    private static readonly HashSet<string> DestinationRefusals = new(StringComparer.Ordinal)
    {
        "RetainerUnreachableAtPlaza", "DestinationBagsUnobserved", "DestinationBagsFull", "DestinationRetainerUnknown",
        "DestinationRetainerFull", "RegionUnreachable",
    };

    private static bool IsDestinationRefusal(string? code) => code is not null && DestinationRefusals.Contains(code);

    private static readonly TimeSpan ForwardPollInterval = TimeSpan.FromSeconds(5);
    private DateTimeOffset nextForwardPollUtc;

    /// <summary>
    /// One variant of a consignment as the plan places it: Local units on the primary's homes, Remotes per other
    /// character in fill order, and RemoteHomes every other character the item has homes on (with room or not).
    /// </summary>
    private sealed record ArrivalShare(uint ItemId, string ItemName, bool IsHq, uint Quantity, uint Local,
        IReadOnlyList<RemoteShare> Remotes, IReadOnlyList<CrossRegionCharacter> RemoteHomes)
    {
        public ulong RemoteUnits => Remotes.Aggregate(0UL, (total, remote) => total + remote.Units);

        public string Variant => $"{ItemName}{(IsHq ? " HQ" : string.Empty)}";
    }

    private sealed record RemoteShare(CrossRegionCharacter Home, uint Units, List<CrossRegionDirectItem> Retainers);

    private bool DirectDeliveryEnabled
    {
        get
        {
            if (session.RequestDocumentSnapshot?.StowAfterDelivery != true || directDelivery is null)
                return false;
            try { return directDelivery.HasDirectDelivery; }
            catch { return false; }
        }
    }

    private bool ForwardEnabled
    {
        get
        {
            try { return directDelivery?.HasDeliveredStowForward == true; }
            catch { return false; }
        }
    }

    private CrossRegionDirectRoute? DirectRouteOf(CourierLeg leg) => leg switch
    {
        CourierLeg.Haul => session.HaulDirect,
        CourierLeg.Collect => CurrentCollection()?.Direct,
        _ => null,
    };

    private CrossRegionCharacter DestinationOf(CourierLeg leg) => DirectRouteOf(leg)?.Destination ?? session.Primary!;

    private static ImmutableArray<QuartermasterCourierItem> ToCourierItems(CrossRegionDirectRoute route) =>
        route.Items.Select(item => new QuartermasterCourierItem(item.ItemId, item.ItemName, item.IsHq, (int)Math.Min(item.Quantity, int.MaxValue), item.RetainerId))
            .ToImmutableArray();

    // ---- Planning ---------------------------------------------------------------------------

    /// <summary>
    /// Where a consignment would go on arrival at the primary. Units this run already delivered there fill the home
    /// chain first (destinations in the plan's fill order), then this consignment takes what room is left. Null when
    /// Quartermaster cannot plan; the consignment then goes to the primary as before.
    /// </summary>
    private Dictionary<(uint ItemId, bool IsHq), ArrivalShare>? PlanArrival(
        IReadOnlyList<CrossRegionManifestLine> consignment,
        IReadOnlyList<CrossRegionManifestLine> delivered,
        string what)
    {
        var primary = session.Primary!;
        static Dictionary<(uint, bool), (string Name, ulong Quantity)> ByVariant(IEnumerable<CrossRegionManifestLine> lines) =>
            lines.GroupBy(line => (line.ItemId, line.IsHq))
                .ToDictionary(group => group.Key, group => (group.First().ItemName, group.Aggregate(0UL, (total, line) => total + line.Quantity)));
        var arriving = ByVariant(consignment);
        var already = ByVariant(delivered.Where(line => arriving.ContainsKey((line.ItemId, line.IsHq))));
        var asked = arriving
            .Select(entry => new QuartermasterArrivalRequestItem(entry.Key.Item1, entry.Key.Item2,
                (int)Math.Min(entry.Value.Quantity + already.GetValueOrDefault(entry.Key).Quantity, int.MaxValue)))
            .ToArray();
        if (asked.Length == 0)
            return null;
        if (!directDelivery!.TryPlanArrival(new QuartermasterOwner(primary.LocalContentId, primary.HomeWorldId, primary.CharacterName, primary.HomeWorldName),
                asked, out var plan, out var error))
        {
            log($"{CodeDirectPlanUnavailable}: {what} goes to {primary.Label}; Quartermaster could not plan where it belongs: {error}", null);
            return null;
        }
        var shares = new Dictionary<(uint ItemId, bool IsHq), ArrivalShare>();
        foreach (var ((itemId, isHq), (name, quantity)) in arriving)
        {
            var answer = plan!.Items.First(item => item.ItemId == itemId && item.IsHq == isHq);
            var destinations = answer.Destinations ?? [];
            // Delivered units take the chain's room before this consignment does.
            var skip = already.GetValueOrDefault((itemId, isHq)).Quantity;
            var left = quantity;
            ulong local = 0;
            var remotes = new List<RemoteShare>();
            foreach (var destination in destinations)
            {
                var units = (ulong)Math.Max(0, destination.Units);
                var skipped = Math.Min(units, skip);
                skip -= skipped;
                var take = Math.Min(units - skipped, left);
                if (take == 0)
                    continue;
                left -= take;
                if (destination.IsOwner)
                {
                    local += take;
                    continue;
                }
                var retainers = new List<CrossRegionDirectItem>();
                var retainerSkip = skipped;
                var retainerLeft = take;
                foreach (var retainer in destination.Retainers)
                {
                    var room = (ulong)Math.Max(0, retainer.Units);
                    var passed = Math.Min(room, retainerSkip);
                    retainerSkip -= passed;
                    var share = Math.Min(room - passed, retainerLeft);
                    if (share == 0)
                        continue;
                    retainerLeft -= share;
                    retainers.Add(new(itemId, name, isHq, (uint)share, retainer.RetainerId, retainer.RetainerName));
                }
                remotes.Add(new(Character(destination.Character!), (uint)(take - retainerLeft), retainers));
            }
            var remoteHomes = destinations.Where(destination => !destination.IsOwner).Select(destination => Character(destination.Character!)).ToList();
            shares[(itemId, isHq)] = new(itemId, name, isHq, (uint)Math.Min(quantity, uint.MaxValue), (uint)local, remotes, remoteHomes);
        }
        return shares;

        static CrossRegionCharacter Character(QuartermasterStowCharacter character) =>
            new(character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName);
    }

    /// <summary>
    /// Whether the stow asks Quartermaster to forward: only when one of the stowed items has homes on another
    /// character, so a stow without any is sent exactly as before. When the plan is unavailable Quartermaster decides.
    /// </summary>
    private bool StowNeedsForward(IReadOnlyList<CrossRegionManifestLine> items)
    {
        if (!ForwardEnabled || items.Count == 0)
            return false;
        bool canPlan;
        try { canPlan = directDelivery!.HasDirectDelivery; }
        catch { canPlan = false; }
        return !canPlan || PlanArrival(items, [], "The stow") is not { } shares || shares.Values.Any(share => share.RemoteHomes.Count > 0);
    }

    /// <summary>
    /// A direct route for a consignment when every unit of it is bound for the homes of one other character in the
    /// courier roster (not the primary, the source or the carrier), whether that character ranks first or the homes
    /// ranked before it take nothing (full or at cap); otherwise null and it goes to the primary.
    /// </summary>
    private CrossRegionDirectRoute? DirectRouteFor(
        IReadOnlyList<CrossRegionManifestLine> consignment,
        IReadOnlyList<CrossRegionManifestLine> delivered,
        CrossRegionCharacter source,
        string what)
    {
        if (!DirectDeliveryEnabled || consignment.Count == 0 || PlanArrival(consignment, delivered, what) is not { } shares)
            return null;
        var remote = shares.Values.SelectMany(share => share.Remotes.Select(part => (Share: share, Part: part))).ToList();
        if (remote.Count == 0)
            return null;
        var homes = remote.Select(entry => entry.Part.Home).DistinctBy(home => (home.LocalContentId, home.HomeWorldId)).ToList();
        var primary = session.Primary!;
        if (homes.Count > 1 || shares.Values.Any(share => share.RemoteUnits < share.Quantity))
        {
            log($"{CodeDirectMixedConsignment}: {what} goes to {primary.Label} in one trip; " +
                string.Join(", ", remote.Select(entry => $"{entry.Part.Units} {entry.Share.Variant} for {entry.Part.Home.Label}")) +
                " follow from there after the stow.", null);
            return null;
        }
        var home = homes[0];
        if (home.Matches(primary) || home.Matches(source))
            return null;
        if (!courier.TryGetCourierStatus(out var status, out var error) ||
            status!.Roster.FirstOrDefault(row => row.LocalContentId == home.LocalContentId && row.HomeWorldId == home.HomeWorldId) is not { IsCarrier: false })
        {
            log($"{CodeDirectDestinationNotInRoster}: {what} goes to {primary.Label}; {home.Label} is not a courier destination in Quartermaster's roster" +
                (status is null ? $" (status unavailable: {error})." : "."), null);
            return null;
        }
        return new CrossRegionDirectRoute { Destination = home, Items = remote.SelectMany(entry => entry.Part.Retainers).ToList() };
    }

    /// <summary>
    /// Counted stock whose source is the item's home character stays there, except what the primary's own homes
    /// still take. Planned per variant: an any-quality line asks for its NQ and HQ units separately and, once part of
    /// it stays, is collected as one line per variant so the other variant is never taken in its place. Retainer stock
    /// is kept first (it is already home), then bag stock. Asked once, and only before anything of the source moved.
    /// </summary>
    private void KeepCountedStockAtHome(CrossRegionCollection collection)
    {
        if (collection.HomeChecked)
            return;
        collection.HomeChecked = true;
        // A source a previous build already started on is left as it was.
        if (collection.State != CrossRegionCollectionState.Pending || !DirectDeliveryEnabled)
        {
            Save();
            return;
        }
        var variants = collection.Lines.Select(line => (Line: line, Units: VariantsOf(line))).ToList();
        // Lines for the same item count the same containers, so each variant asks for its largest count once.
        var consignment = variants
            .SelectMany(entry => entry.Units.Select(unit => (entry.Line.ItemId, entry.Line.ItemName, unit.IsHq, Quantity: unit.Bag + unit.Retainer)))
            .Where(unit => unit.Quantity > 0)
            .GroupBy(unit => (unit.ItemId, unit.IsHq))
            .Select(group => new CrossRegionManifestLine(group.Key.ItemId, group.First().ItemName, group.Key.IsHq, group.Max(unit => unit.Quantity)))
            .ToList();
        var delivered = collection == session.ShopperCounted ? [] : DeliveredItems();
        if (consignment.Count == 0 || PlanArrival(consignment, delivered, $"Counted stock on {collection.Source.Label}") is not { } shares)
        {
            Save();
            return;
        }
        // Per variant whose homes include this source: the counted units beyond what homes on other characters (the
        // primary's or another's) would take.
        var keep = consignment.ToDictionary(line => (line.ItemId, line.IsHq),
            line => shares.TryGetValue((line.ItemId, line.IsHq), out var share) && share.RemoteHomes.Any(home => home.Matches(collection.Source))
                ? line.Quantity - (uint)Math.Min(line.Quantity, share.Local + share.Remotes
                    .Where(part => !part.Home.Matches(collection.Source))
                    .Aggregate(0UL, (total, part) => total + part.Units))
                : 0u);
        if (keep.Values.All(units => units == 0))
        {
            Save();
            return;
        }
        var lines = new List<CrossRegionCountedLine>();
        var kept = new Dictionary<(uint ItemId, bool? IsHq), (string Name, uint Units)>();
        var keptInRetainers = new HashSet<(uint ItemId, bool IsHq)>();
        void Record((uint, bool?) key, string name, uint units) =>
            kept[key] = (name, Math.Max(units, kept.GetValueOrDefault(key).Units));
        foreach (var (line, units) in variants)
        {
            var staying = units.Select(unit => (Unit: unit, Kept: Math.Min(unit.Bag + unit.Retainer, keep.GetValueOrDefault((line.ItemId, unit.IsHq))))).ToList();
            if (staying.All(entry => entry.Kept == 0))
            {
                lines.Add(line);
                continue;
            }
            if (!HqSplitKnown(line))
            {
                // Which units are HQ was not observed, so no part of the line can be taken without risking the other's.
                Record((line.ItemId, null), line.ItemName, line.Total);
                if (line.RetainerQuantity > 0)
                {
                    keptInRetainers.Add((line.ItemId, false));
                    keptInRetainers.Add((line.ItemId, true));
                }
                continue;
            }
            foreach (var (unit, stays) in staying)
            {
                var fromRetainers = Math.Min(stays, unit.Retainer);
                if (fromRetainers > 0)
                    keptInRetainers.Add((line.ItemId, unit.IsHq));
                if (stays > 0)
                    Record((line.ItemId, unit.IsHq), line.ItemName, stays);
                var bag = unit.Bag - (stays - fromRetainers);
                var retainer = unit.Retainer - fromRetainers;
                if (bag + retainer > 0)
                    lines.Add(new CrossRegionCountedLine(line.ItemId, line.ItemName, unit.IsHq ? "HqOnly" : "NqOnly", bag, retainer,
                        unit.IsHq ? bag : 0, unit.IsHq ? retainer : 0, HqSplitKnown: true));
            }
        }
        foreach (var ((itemId, isHq), (name, units)) in kept)
        {
            var variant = isHq switch { true => " HQ", false => " NQ", null => string.Empty };
            session.KeptAtHome.Add(new(itemId, name, collection.Source.Label, units, CodeKeptAtHome));
            log($"{CodeKeptAtHome}: {units} counted {name}{variant} stay on {collection.Source.Label}, their home character" +
                (isHq is null ? "; the line's HQ/NQ split was not observed, so none of it is collected." : "."), null);
        }
        // Retrieval asks for an item, not a quality: it could hand over the units that stay instead of the other variant's.
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var policy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);
            var isHq = policy == "HqOnly";
            if (line.RetainerQuantity == 0 || policy == "Either" || !keptInRetainers.Contains((line.ItemId, !isHq)))
                continue;
            session.NotCollected.Add(new(line.ItemId, line.ItemName, collection.Source.Label, line.RetainerQuantity, CodeRetrievalWouldTakeHomeStock));
            log($"{CodeRetrievalWouldTakeHomeStock}: {line.RetainerQuantity} {line.ItemName}{(isHq ? " HQ" : " NQ")} stay in {collection.Source.Label}'s " +
                $"retainers; retrieving them could take the {(isHq ? "NQ" : "HQ")} units that stay home instead.", null);
            lines[i] = line with { RetainerQuantity = 0, RetainerHqQuantity = 0 };
        }
        collection.Lines = lines.Where(line => line.Total > 0).ToList();
        if (collection.Lines.Count == 0)
        {
            collection.State = CrossRegionCollectionState.Completed;
            collection.OutcomeCode = CodeKeptAtHome;
            collection.RetrievalSettled = true;
            collection.Manifest = [];
            session.LastMessage = $"Counted stock on {collection.Source.Label} is already home.";
        }
        Save();
    }

    private static bool HqSplitKnown(CrossRegionCountedLine line) =>
        line.HqSplitKnown || MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy) is "HqOnly" or "NqOnly";

    /// <summary>
    /// A counted line's units per variant and container. An any-quality line whose split was not observed asks for its
    /// full count as either variant, so a home on the source is found whichever units it holds.
    /// </summary>
    private static (bool IsHq, uint Bag, uint Retainer)[] VariantsOf(CrossRegionCountedLine line) =>
        MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy) switch
        {
            "HqOnly" => [(true, line.BagQuantity, line.RetainerQuantity)],
            "NqOnly" => [(false, line.BagQuantity, line.RetainerQuantity)],
            _ when !line.HqSplitKnown => [(false, line.BagQuantity, line.RetainerQuantity), (true, line.BagQuantity, line.RetainerQuantity)],
            _ =>
            [
                (false, line.BagQuantity - Math.Min(line.BagHqQuantity, line.BagQuantity), line.RetainerQuantity - Math.Min(line.RetainerHqQuantity, line.RetainerQuantity)),
                (true, Math.Min(line.BagHqQuantity, line.BagQuantity), Math.Min(line.RetainerHqQuantity, line.RetainerQuantity)),
            ],
        };

    // ---- Fallback ---------------------------------------------------------------------------

    /// <summary>
    /// Quartermaster refused a direct delivery for its destination before anything moved: record it, log the lines, destination and code,
    /// and send the same consignment to the primary once under a new request id. The stow's forward carries it on.
    /// </summary>
    private void FallBackToPrimary(CourierLeg leg, string code, string detail)
    {
        var route = DirectRouteOf(leg)!;
        var primary = session.Primary!;
        foreach (var group in route.Items.GroupBy(item => (item.ItemId, item.IsHq)))
            session.DirectFallbacks.Add(new(group.Key.ItemId, group.First().ItemName, route.Destination.Label,
                (uint)Math.Min(group.Aggregate(0UL, (total, item) => total + item.Quantity), uint.MaxValue), code));
        log($"{code}: Quartermaster refused delivering {LegNoun(leg)} straight to {route.Destination.Label} " +
            $"({string.Join(", ", route.Items.Select(item => $"{item.Quantity} {item.ItemName}{(item.IsHq ? " HQ" : string.Empty)} → {item.RetainerName ?? $"retainer {item.RetainerId}"}"))}): " +
            $"{detail} Nothing moved; it goes to {primary.Label} instead.", null);
        if (leg == CourierLeg.Haul)
        {
            session.HaulDirect = null;
            session.HaulAttempt++;
            session.HaulRequestId = CrossRegionAcquisitionSession.HaulRequestIdFor(session.SessionId, session.HaulAttempt);
            session.HaulRunId = null;
        }
        else if (CurrentCollection() is { } collection)
        {
            collection.Direct = null;
            collection.Attempt++;
            collection.RequestId = CrossRegionAcquisitionSession.CollectRequestIdFor(session.SessionId, session.Collections.IndexOf(collection), collection.Attempt);
            collection.RunId = null;
        }
        submitFailingSinceUtc = null;
        historyIndexingRetries = 0;
        awaitingRunAppearanceSinceUtc = null;
        nextSubmitRetryUtc = DateTimeOffset.MinValue;
        session.CourierPhaseSummary = null;
        session.LastMessage = $"Delivering to {primary.Label} instead of {route.Destination.Label} ({code}).";
        Save();
    }

    /// <summary>Where a failed direct leg's goods may be, for its failure message.</summary>
    private string DirectFailureNote(CourierLeg leg) => DirectRouteOf(leg) is { } route
        ? $" Delivered goods may already be in {route.Destination.CharacterName}'s bags or retainers."
        : string.Empty;

    // ---- What reached the homes directly ------------------------------------------------------

    /// <summary>Units the courier confirmed delivered straight to their homes: completed direct legs only.</summary>
    private List<(CrossRegionCharacter Destination, CrossRegionManifestLine Line)> DirectDeliveries() =>
        (session.HaulDelivered && session.HaulDirect is { } haul ? session.HaulManifest.Select(line => (haul.Destination, line)) : [])
            .Concat(session.Collections
                .Where(collection => collection.State == CrossRegionCollectionState.Completed && collection.Direct is not null)
                .SelectMany(collection => collection.Manifest.Select(line => (collection.Direct!.Destination, line))))
            .ToList();

    private List<CrossRegionManifestLine> SentDirectItems() => MergeByVariant(DirectDeliveries().Select(entry => entry.Line));

    private static List<CrossRegionManifestLine> MergeByVariant(IEnumerable<CrossRegionManifestLine> lines) =>
        lines.GroupBy(line => (line.ItemId, line.IsHq))
            .Select(group => new CrossRegionManifestLine(group.Key.ItemId, group.First().ItemName, group.Key.IsHq,
                (uint)Math.Min(group.Aggregate(0UL, (total, line) => total + line.Quantity), uint.MaxValue)))
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToList();

    private static IReadOnlyList<QuartermasterDeliveredStowItem> StowRequestItems(CrossRegionStow stow) =>
        stow.Items.Select(line => (line.ItemId, line.ItemName, line.IsHq, Bags: (ulong)line.Quantity, Direct: 0UL))
            .Concat(stow.SentDirect.Select(line => (line.ItemId, line.ItemName, line.IsHq, Bags: 0UL, Direct: (ulong)line.Quantity)))
            .GroupBy(entry => (entry.ItemId, entry.IsHq))
            .Select(group => new QuartermasterDeliveredStowItem(group.Key.ItemId, group.First().ItemName, group.Key.IsHq,
                (int)Math.Min(group.Aggregate(0UL, (total, entry) => total + entry.Bags), int.MaxValue),
                (int)Math.Min(group.Aggregate(0UL, (total, entry) => total + entry.Direct), int.MaxValue)))
            .ToArray();

    // ---- Forward watch ----------------------------------------------------------------------

    /// <summary>
    /// How long an open forward is followed: Quartermaster holds a forward up to 30 minutes for a busy courier before
    /// its run starts, and the run gets another 60 (several trips plus a stop it can resume from).
    /// </summary>
    private static readonly TimeSpan ForwardWatchDeadline = TimeSpan.FromMinutes(90);
    /// <summary>Consecutive failed status reads (five minutes at the poll interval) after which the forward is given up.</summary>
    private const int MaxForwardFailedReads = 60;
    /// <summary>How long a new courier submit waits for an earlier run's open forward to free the courier.</summary>
    private static readonly TimeSpan ForwardBusyWaitLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ForwardBusyRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SettledForwardRetention = TimeSpan.FromHours(24);
    private const int MaxForwardWatches = 20;

    private List<CrossRegionForwardWatch> forwardWatches = [];
    private DateTimeOffset? forwardBusySinceUtc;

    /// <summary>Open forwards of finished runs and recently settled ones, newest last; independent of the session.</summary>
    public IReadOnlyList<CrossRegionForwardWatch> Forwards => forwardWatches;

    private void LoadForwardWatches()
    {
        if (store is not ICrossRegionForwardStore forwardStore)
            return;
        try { forwardWatches = forwardStore.LoadForwards(); }
        catch (Exception ex) { log("Open stow forwards could not be loaded.", ex); }
        // A session persisted before forwards were tracked separately.
        if (session.Stow is { ForwardOpen: true } stow)
            TrackForward(stow, stow.SubmittedAtUtc ?? utcNow());
    }

    private void SaveForwardWatches()
    {
        if (store is not ICrossRegionForwardStore forwardStore)
            return;
        try { forwardStore.SaveForwards(forwardWatches); }
        catch (Exception ex) { log("Open stow forwards could not be saved.", ex); }
    }

    /// <summary>The stow's local part settled with its forward still open: follow it until Quartermaster settles it.</summary>
    private void TrackForward(CrossRegionStow stow, DateTimeOffset openedAtUtc)
    {
        if (stow.RequestId is not { } requestId || forwardWatches.Any(watch => watch.RequestId == requestId))
            return;
        forwardWatches.Add(new CrossRegionForwardWatch
        {
            RequestId = requestId,
            SessionId = session.SessionId,
            Owner = session.Primary?.Label ?? "the primary",
            OpenedAtUtc = openedAtUtc,
            Lines = stow.Lines.Where(line => line.HasForward).ToList(),
        });
        PruneForwardWatches(utcNow());
        SaveForwardWatches();
    }

    /// <summary>Settled forwards are dropped with the session that showed them; open ones always stay.</summary>
    private void DropSettledForwards()
    {
        if (forwardWatches.RemoveAll(watch => watch.Settled) > 0)
            SaveForwardWatches();
    }

    private void PruneForwardWatches(DateTimeOffset now)
    {
        forwardWatches.RemoveAll(watch => watch.Settled && now - (watch.SettledAtUtc ?? watch.OpenedAtUtc) >= SettledForwardRetention);
        while (forwardWatches.Count > MaxForwardWatches && forwardWatches.FindIndex(watch => watch.Settled) is var oldest and >= 0)
            forwardWatches.RemoveAt(oldest);
    }

    /// <summary>
    /// Follows every open forward whatever the session is doing, without switching characters or holding automation,
    /// and settles each once: from Quartermaster's answer, or as ForwardNotObserved after the deadline or a run of
    /// failed reads.
    /// </summary>
    private void TickForwardWatches()
    {
        if (forwardWatches.All(watch => watch.Settled))
            return;
        var now = utcNow();
        if (now < nextForwardPollUtc)
            return;
        nextForwardPollUtc = now + ForwardPollInterval;
        var changed = false;
        foreach (var watch in forwardWatches.Where(watch => !watch.Settled).ToList())
            changed |= PollForward(watch, now);
        if (!changed)
            return;
        PruneForwardWatches(now);
        SaveForwardWatches();
    }

    private bool PollForward(CrossRegionForwardWatch watch, DateTimeOffset now)
    {
        QuartermasterDeliveredStowStatus? status = null;
        string? error;
        try
        {
            if (custody is null)
                error = "Quartermaster is unavailable.";
            else if (!custody.TryGetDeliveredStow(watch.RequestId, out status, out error))
                status = null;
            else if (status!.IsNotFound)
            {
                SettleForward(watch, CodeForwardNotObserved, $"Quartermaster no longer reports stow {watch.RequestId}.", now);
                return true;
            }
        }
        catch (Exception ex)
        {
            status = null;
            error = ex.Message;
        }
        if (status is null)
        {
            watch.FailedReads++;
            watch.LastError = error;
            if (watch.FailedReads >= MaxForwardFailedReads)
                SettleForward(watch, CodeForwardNotObserved, $"{MaxForwardFailedReads} status reads in a row failed; last error: {error}", now);
            else if (WatchedFor(watch, now) >= ForwardWatchDeadline)
                SettleForward(watch, CodeForwardNotObserved,
                    $"not settled within {ForwardWatchDeadline.TotalMinutes:N0} minutes{ParkedNote(watch)}; last read failed: {error}", now);
            else
                return false;
            return true;
        }
        watch.FailedReads = 0;
        watch.LastError = null;
        var observed = status.Lines.Where(line => line.HasForward).ToList();
        var changed = observed.Count > 0 && !observed.Select(ForwardKey).SequenceEqual(watch.Lines.Select(ForwardKey));
        if (changed)
            watch.Lines = observed;
        if (SessionStowFor(watch) is { } stow && changed)
        {
            MergeForwardLines(stow, status);
            Save();
        }
        if (status.Forward is null)
        {
            SettleForward(watch, CodeForwardNotObserved, $"Quartermaster reports no forward for stow {watch.RequestId}.", now);
            return true;
        }
        if (status.Forward.IsSettled)
        {
            SettleForward(watch, status.Forward.Code ?? status.Forward.State, null, now);
            return true;
        }
        // A courier run parked for the user (stopped, resumable) is waiting on a person, not lost: that time is not
        // counted, so resuming it in Quartermaster still settles the forward here.
        var parked = IsForwardRunParked(status.Forward);
        if (parked && watch.ParkedSinceUtc is null)
        {
            watch.ParkedSinceUtc = now;
            changed = true;
        }
        else if (!parked && watch.ParkedSinceUtc is { } parkedSince)
        {
            watch.ParkedFor += now - parkedSince;
            watch.ParkedSinceUtc = null;
            changed = true;
        }
        if (WatchedFor(watch, now) < ForwardWatchDeadline)
            return changed;
        SettleForward(watch, CodeForwardNotObserved,
            $"still {status.Forward.State} ({status.Forward.Code ?? "no code"}) after {ForwardWatchDeadline.TotalMinutes:N0} minutes{ParkedNote(watch)}; " +
            $"last read: {status.Forward.Message ?? "no detail"}", now);
        return true;
    }

    /// <summary>How long the forward has been followed, not counting time its courier run sat parked for the user.</summary>
    private static TimeSpan WatchedFor(CrossRegionForwardWatch watch, DateTimeOffset now) =>
        now - watch.OpenedAtUtc - watch.ParkedFor - (watch.ParkedSinceUtc is { } since ? now - since : TimeSpan.Zero);

    private static string ParkedNote(CrossRegionForwardWatch watch) =>
        watch.ParkedFor > TimeSpan.Zero ? $" (not counting {watch.ParkedFor.TotalMinutes:N0} minutes parked in Quartermaster)" : string.Empty;

    /// <summary>The forward's courier run is Quartermaster's current run and is stopped where the user can resume it.</summary>
    private bool IsForwardRunParked(QuartermasterStowForward forward)
    {
        if (!Guid.TryParse(forward.RunId, out var runId))
            return false;
        try
        {
            return courier.TryGetCourierStatus(out var status, out _) &&
                   status?.Run is { } run && run.RunId == runId && run.IsFailure && run.CanResume;
        }
        catch
        {
            return false;
        }
    }

    private static string ForwardKey(QuartermasterDeliveredStowLine line) =>
        $"{line.ItemId}:{line.IsHq}:" + string.Join("|", line.AllForwards.Select(forward =>
            $"{forward.Character?.LocalContentId}/{forward.Code}/{forward.Delivered}/{forward.Planned}"));

    private CrossRegionStow? SessionStowFor(CrossRegionForwardWatch watch) =>
        session.Stow is { } stow && string.Equals(stow.RequestId, watch.RequestId, StringComparison.Ordinal) ? stow : null;

    /// <summary>Settles a watched forward once: logs it, records its outcome for the status panel and updates its session.</summary>
    private void SettleForward(CrossRegionForwardWatch watch, string code, string? failure, DateTimeOffset now)
    {
        if (failure is not null)
            watch.Lines = watch.Lines
                .Select(line => line.AllForwards.Any(Unobserved)
                    ? line.WithForwards(line.AllForwards.Select(forward => Unobserved(forward) ? forward with { Code = CodeForwardNotObserved } : forward).ToList())
                    : line)
                .ToList();
        watch.Settled = true;
        watch.Code = code;
        watch.SettledAtUtc = now;
        watch.Summary = failure is not null
            ? $"Forward of {watch.Owner}'s stow not observed: {failure}"
            : $"Forward of {watch.Owner}'s stow: " + string.Join("; ", ForwardNotes(watch.Lines, open: false).DefaultIfEmpty(code));
        if (failure is not null)
            log($"{CodeForwardNotObserved}: stopped following the forward of stow {watch.RequestId}: {failure} " +
                $"What it had not delivered is in {watch.Owner}'s bags or Quartermaster's courier custody.", null);
        else
            LogForwardOutcomes(watch.Lines, watch.Owner);
        if (SessionStowFor(watch) is not { } stow)
            return;
        stow.ForwardOpen = false;
        stow.ForwardCode = code;
        stow.Lines = stow.Lines
            .Select(line => watch.Lines.FirstOrDefault(watched => watched.ItemId == line.ItemId && watched.IsHq == line.IsHq) is { HasForward: true } watched
                ? line.WithForwards(watched.AllForwards)
                : line)
            .ToList();
        if (session.IsTerminal)
            session.LastMessage = FinalSummary();
        Save();
    }

    /// <summary>
    /// A new courier submit found the courier busy while an earlier run's forward is still open: the forward is
    /// probably that run, so the same request is sent again shortly, for at most <see cref="ForwardBusyWaitLimit"/>.
    /// </summary>
    private bool WaitForOpenForward(string requestId)
    {
        if (forwardWatches.FirstOrDefault(watch => !watch.Settled) is not { } open)
            return false;
        var now = utcNow();
        forwardBusySinceUtc ??= now;
        var waited = now - forwardBusySinceUtc.Value;
        if (waited >= ForwardBusyWaitLimit)
        {
            forwardBusySinceUtc = null;
            Fail(session.Phase, CodeForwardStillRunning,
                $"Quartermaster's courier was still busy after waiting {ForwardBusyWaitLimit.TotalMinutes:N0} minutes for the forward of {open.Owner}'s " +
                $"earlier stow to settle; resume to send {requestId} again.", canResumeCourier: false);
            return true;
        }
        nextSubmitRetryUtc = now + ForwardBusyRetryInterval;
        session.LastMessage = $"Waiting for the courier to finish forwarding {open.Owner}'s earlier stow " +
                              $"({waited.TotalMinutes:N0}/{ForwardBusyWaitLimit.TotalMinutes:N0} min).";
        return true;
    }

    /// <summary>One status line for a watched forward: its outcome once settled, else what is still on the way.</summary>
    public static string DescribeForward(CrossRegionForwardWatch watch) =>
        watch.Settled && watch.Summary is { } summary ? summary
            : $"Forwarding {watch.Owner}'s stow: " + string.Join("; ", ForwardNotes(watch.Lines, open: true).DefaultIfEmpty("waiting for Quartermaster"));

    /// <summary>One note per character each forwarded line is bound for, skipping parts nothing was meant to move for.</summary>
    private static IEnumerable<string> ForwardNotes(IEnumerable<QuartermasterDeliveredStowLine> lines, bool open) =>
        lines.SelectMany(line => line.AllForwards
            .Where(forward => !forward.IsNeutral)
            .Select(forward => $"{line.ItemName}{(line.IsHq ? " HQ" : string.Empty)} {ForwardNote(forward, open)}"));

    /// <summary>A forward part whose outcome nobody will report any more once the watch gives up.</summary>
    private static bool Unobserved(QuartermasterStowForwardLine forward) =>
        forward is { IsOpen: true } or { Code: "RunFailed" or "RunInterrupted" or "RunIndeterminate" };

    /// <summary>A direct leg refused for a reason other than its destination: where its goods may already be.</summary>
    private string RejectionNote(CourierLeg leg, string? code) =>
        code is CodeCourierRunCleared or CodeRequestConflict ? DirectFailureNote(leg) : string.Empty;

    private static void MergeForwardLines(CrossRegionStow stow, QuartermasterDeliveredStowStatus status)
    {
        var observed = status.Lines.GroupBy(line => (line.ItemId, line.IsHq)).ToDictionary(group => group.Key, group => group.First());
        stow.Lines = stow.Lines
            .Select(line => observed.TryGetValue((line.ItemId, line.IsHq), out var current) && current.HasForward ? line.WithForwards(current.AllForwards) : line)
            .ToList();
    }

    /// <summary>Every forwarded line that did not fully arrive, with its character, homes and code.</summary>
    private void LogForwardOutcomes(IEnumerable<QuartermasterDeliveredStowLine> lines, string? owner)
    {
        foreach (var line in lines)
        foreach (var forward in line.AllForwards)
        {
            if (forward.IsOpen || forward.IsNeutral || forward.Code == "Delivered")
                continue;
            var homes = string.Join(" → ", forward.Homes.Select(home => home.RetainerName ?? $"retainer {home.RetainerId}"));
            log($"{forward.Code}: {forward.Delivered ?? 0} of {forward.Wanted} {line.ItemName}{(line.IsHq ? " HQ" : string.Empty)} forwarded to " +
                $"{forward.Character?.CharacterName} ({homes}); the rest stays with {owner}. {forward.Message}", null);
        }
    }

    /// <summary>Units of a stow line meant for the owner's own homes: what Quartermaster's forwards routed elsewhere is not.</summary>
    private static int LocalRequested(QuartermasterDeliveredStowLine line) =>
        Math.Max(0, line.Requested - line.AllForwards.Where(forward => forward.Code != "NoRoom").Sum(forward => Math.Max(0, forward.Wanted)));
}
