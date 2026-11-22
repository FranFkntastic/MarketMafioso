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
/// character R only when every unit of it is bound for R's homes (the plan fills the primary's own homes first). Then
/// the pickup that would have gone to the primary goes to R instead, and the later forward from the primary to R never
/// happens. A consignment that is only partly bound for R still goes to the primary in one trip; Quartermaster's
/// forward carries R's share on after the stow, which costs the same trades as splitting the pickup would. Counted
/// stock already on its home character is not collected at all.
///
/// Journaling: the route (destination and per-retainer units) is persisted with the leg's request id before the
/// courier sees it, so a replay sends the identical request. A refused direct delivery (nothing moved) is recorded,
/// logged and resent to the primary once under a new request id; it is never retried towards R. Only a completed run
/// counts as delivered there.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    public const string CodeKeptAtHome = "KeptAtHome";
    public const string CodeDirectMixedConsignment = "DirectMixedConsignment";
    public const string CodeDirectPlanUnavailable = "DirectPlanUnavailable";
    public const string CodeDirectDestinationNotInRoster = "DirectDestinationNotInRoster";
    public const string CodeForwardNotObserved = "ForwardNotObserved";

    private static readonly TimeSpan ForwardPollInterval = TimeSpan.FromSeconds(5);
    private DateTimeOffset nextForwardPollUtc;

    private sealed record ArrivalShare(uint ItemId, string ItemName, bool IsHq, uint Quantity, uint Local, uint Remote,
        CrossRegionCharacter? Home, List<CrossRegionDirectItem> Retainers);

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
    /// Where a consignment would go on arrival at the primary. Units this run already delivered there take the
    /// primary's room (and then the remote room) first. Null when Quartermaster cannot plan; the consignment then
    /// goes to the primary as before.
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
            var before = already.GetValueOrDefault((itemId, isHq)).Quantity;
            var localUnits = (ulong)Math.Max(0, answer.Local?.Units ?? 0);
            var local = Math.Min(quantity, localUnits - Math.Min(localUnits, before));
            // Delivered units the primary's homes cannot take use the remote room before this consignment does.
            var skip = before - Math.Min(before, localUnits);
            var remoteRoom = (ulong)Math.Max(0, answer.Remote?.Units ?? 0);
            var remote = Math.Min(quantity - local, remoteRoom - Math.Min(remoteRoom, skip));
            var retainers = new List<CrossRegionDirectItem>();
            var left = remote;
            foreach (var retainer in answer.Remote?.Retainers ?? [])
            {
                var units = (ulong)retainer.Units;
                var skipped = Math.Min(units, skip);
                skip -= skipped;
                var take = Math.Min(units - skipped, left);
                if (take == 0)
                    continue;
                left -= take;
                retainers.Add(new(itemId, name, isHq, (uint)take, retainer.RetainerId, retainer.RetainerName));
            }
            var home = answer.Remote?.Character is { } character
                ? new CrossRegionCharacter(character.LocalContentId, character.HomeWorldId, character.CharacterName, character.HomeWorldName)
                : null;
            shares[(itemId, isHq)] = new(itemId, name, isHq, (uint)Math.Min(quantity, uint.MaxValue), (uint)local, (uint)(remote - left), home, retainers);
        }
        return shares;
    }

    /// <summary>
    /// A direct route for a consignment when every unit of it is bound for the homes of one other character in the
    /// courier roster (not the primary, the source or the carrier); otherwise null and it goes to the primary.
    /// </summary>
    private CrossRegionDirectRoute? DirectRouteFor(
        IReadOnlyList<CrossRegionManifestLine> consignment,
        IReadOnlyList<CrossRegionManifestLine> delivered,
        CrossRegionCharacter source,
        string what)
    {
        if (!DirectDeliveryEnabled || consignment.Count == 0 || PlanArrival(consignment, delivered, what) is not { } shares)
            return null;
        var remote = shares.Values.Where(share => share.Remote > 0).ToList();
        if (remote.Count == 0)
            return null;
        var homes = remote.Select(share => share.Home!).DistinctBy(home => (home.LocalContentId, home.HomeWorldId)).ToList();
        var primary = session.Primary!;
        if (homes.Count > 1 || shares.Values.Any(share => share.Remote < share.Quantity))
        {
            log($"{CodeDirectMixedConsignment}: {what} goes to {primary.Label} in one trip; " +
                string.Join(", ", remote.Select(share => $"{share.Remote} {share.ItemName}{(share.IsHq ? " HQ" : string.Empty)} for {share.Home!.Label}")) +
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
        return new CrossRegionDirectRoute { Destination = home, Items = remote.SelectMany(share => share.Retainers).ToList() };
    }

    /// <summary>
    /// Counted stock whose source is the item's home character stays there, except what the primary's own homes
    /// still take. Retainer stock is kept first (it is already home), then bag stock. Asked once per source.
    /// </summary>
    private void KeepCountedStockAtHome(CrossRegionCollection collection)
    {
        if (collection.HomeChecked)
            return;
        collection.HomeChecked = true;
        var definite = collection.Lines
            .Select(line => (Line: line, Policy: MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy)))
            .Where(entry => entry.Policy is "HqOnly" or "NqOnly" && entry.Line.Total > 0)
            .ToList();
        if (!DirectDeliveryEnabled || definite.Count == 0)
        {
            Save();
            return;
        }
        var consignment = definite
            .Select(entry => new CrossRegionManifestLine(entry.Line.ItemId, entry.Line.ItemName, entry.Policy == "HqOnly", entry.Line.Total))
            .ToList();
        var delivered = collection == session.ShopperCounted ? [] : DeliveredItems();
        var shares = PlanArrival(consignment, delivered, $"Counted stock on {collection.Source.Label}");
        if (shares is null)
        {
            Save();
            return;
        }
        var lines = new List<CrossRegionCountedLine>();
        foreach (var line in collection.Lines)
        {
            var policy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);
            var keep = policy is "HqOnly" or "NqOnly" && shares.TryGetValue((line.ItemId, policy == "HqOnly"), out var share) &&
                       share.Home?.Matches(collection.Source) == true
                ? line.Total - Math.Min(line.Total, share.Local)
                : 0u;
            if (keep == 0)
            {
                lines.Add(line);
                continue;
            }
            var fromRetainers = Math.Min(keep, line.RetainerQuantity);
            var bag = line.BagQuantity - (keep - fromRetainers);
            var kept = line with
            {
                RetainerQuantity = line.RetainerQuantity - fromRetainers,
                BagQuantity = bag,
                BagHqQuantity = Math.Min(line.BagHqQuantity, bag),
            };
            session.KeptAtHome.Add(new(line.ItemId, line.ItemName, collection.Source.Label, keep, CodeKeptAtHome));
            log($"{CodeKeptAtHome}: {keep} counted {line.ItemName} ({line.HqPolicy}) stay on {collection.Source.Label}, their home character; " +
                $"{kept.Total} are collected.", null);
            if (kept.Total > 0)
                lines.Add(kept);
        }
        collection.Lines = lines;
        if (lines.Count == 0)
        {
            collection.State = CrossRegionCollectionState.Completed;
            collection.OutcomeCode = CodeKeptAtHome;
            collection.RetrievalSettled = true;
            collection.Manifest = [];
            session.LastMessage = $"Counted stock on {collection.Source.Label} is already home.";
        }
        Save();
    }

    // ---- Fallback ---------------------------------------------------------------------------

    /// <summary>
    /// Quartermaster refused a direct delivery before anything moved: record it, log the lines, destination and code,
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
        submitAttempts = 0;
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
    /// The run is over but Quartermaster's courier forward is still on its way: follow it without switching
    /// characters or holding automation, and log what did not arrive once it settles.
    /// </summary>
    private void TickForwardWatch()
    {
        if (session.Stow is not { ForwardOpen: true, RequestId: { } requestId } stow || custody is null)
            return;
        var now = utcNow();
        if (now < nextForwardPollUtc)
            return;
        nextForwardPollUtc = now + ForwardPollInterval;
        try
        {
            if (!custody.TryGetDeliveredStow(requestId, out var status, out _))
                return;
            if (status!.IsNotFound)
            {
                stow.ForwardOpen = false;
                stow.ForwardCode = CodeForwardNotObserved;
                log($"{CodeForwardNotObserved}: Quartermaster no longer reports the forward of stow {requestId}; what it had not delivered is in " +
                    $"{session.Primary?.Label}'s bags or Quartermaster's courier custody.", null);
                Save();
                return;
            }
            var before = stow.Lines.Select(line => line.Forward).ToList();
            MergeForwardLines(stow, status);
            if (status.Forward is { IsSettled: false })
            {
                if (!before.SequenceEqual(stow.Lines.Select(line => line.Forward)))
                    Save();
                return;
            }
            stow.ForwardOpen = false;
            stow.ForwardCode = status.Forward?.Code;
            LogForwardOutcomes(stow);
            session.LastMessage = FinalSummary();
            Save();
        }
        catch (Exception ex)
        {
            log($"Following the forward of stow {requestId} failed.", ex);
        }
    }

    private static void MergeForwardLines(CrossRegionStow stow, QuartermasterDeliveredStowStatus status)
    {
        var observed = status.Lines.GroupBy(line => (line.ItemId, line.IsHq)).ToDictionary(group => group.Key, group => group.First().Forward);
        stow.Lines = stow.Lines
            .Select(line => observed.TryGetValue((line.ItemId, line.IsHq), out var forward) && forward is not null ? line with { Forward = forward } : line)
            .ToList();
    }

    /// <summary>Every forwarded line that did not fully arrive, with its character, homes and code.</summary>
    private void LogForwardOutcomes(CrossRegionStow stow)
    {
        foreach (var line in stow.Lines)
        {
            if (line.Forward is not { } forward || forward.IsOpen || forward.IsNeutral || forward.Code == "Delivered")
                continue;
            var homes = string.Join(" → ", forward.Homes.Select(home => home.RetainerName ?? $"retainer {home.RetainerId}"));
            log($"{forward.Code}: {forward.Delivered ?? 0} of {forward.Wanted} {line.ItemName}{(line.IsHq ? " HQ" : string.Empty)} forwarded to " +
                $"{forward.Character?.CharacterName} ({homes}); the rest stays with {session.Primary?.Label}. {forward.Message}", null);
        }
    }

    /// <summary>Units of a stow line meant for the owner's own homes: what Quartermaster's forward routed elsewhere is not.</summary>
    private static int LocalRequested(QuartermasterDeliveredStowLine line) =>
        Math.Max(0, line.Requested - (line.Forward is { Code: not "NoRoom" } forward ? Math.Max(0, forward.Wanted) : 0));
}
