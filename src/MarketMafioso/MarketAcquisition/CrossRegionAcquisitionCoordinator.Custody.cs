using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>One plan line's end-of-run result where it differs from the plan.</summary>
public sealed record CrossRegionLineResult(
    uint ItemId,
    string ItemName,
    uint Bought,
    uint CountedElsewhere,
    uint Collected,
    uint StowPlanned,
    uint Stowed,
    IReadOnlyList<string> Codes);

/// <summary>
/// Counted-stock collection and delivered stow. Sequencing: counted stock on the shopper rides home with the
/// haul; every other source is collected after the haul, one courier transfer per source, and the stow runs
/// once at the end. Each other source costs one switch onto it and the courier ends back on the primary, which
/// is the minimum wherever the collection is placed; collecting after the haul settles the gil-funded haul first
/// and lets one stow open each home retainer once for everything delivered.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    public const string CodeCountedStockOnCarrier = "CountedStockOnCarrier";
    public const string CodeCollectRejected = "CollectRejected";
    public const string CodeCollectSourceSwitchFailed = "CollectSourceSwitchFailed";
    public const string CodeCollectReturnSwitchFailed = "CollectReturnSwitchFailed";
    public const string CodeCollectBagsUnavailable = "CollectBagsUnavailable";
    public const string CodeCollectRunFailed = "CollectRunFailed";
    public const string CodeCountedStockNotFound = "CountedStockNotFound";
    public const string CodeCountedShort = "CountedShort";
    public const string CodeCountedRetrievalUnavailable = "CountedRetrievalUnavailable";
    public const string CodeCountedRetrievalRejected = "CountedRetrievalRejected";
    public const string CodeCountedRetrievalSubmitFailed = "CountedRetrievalSubmitFailed";
    public const string CodeCountedRetrievalIncomplete = "CountedRetrievalIncomplete";
    public const string CodeCountedRetrievalTimedOut = "CountedRetrievalTimedOut";
    public const string CodeCountedRetrievalCancelled = "CountedRetrievalCancelled";
    public const string CodeStowUnavailable = "StowUnavailable";
    public const string CodeStowPrimarySwitchFailed = "StowPrimarySwitchFailed";
    public const string CodeStowRejected = "StowRejected";
    public const string CodeStowSubmitFailed = "StowSubmitFailed";
    public const string CodeStowNoRoom = "StowNoRoom";
    public const string CodeStowPartial = "StowPartial";
    public const string CodeStowFailed = "StowFailed";
    public const string CodeStowIndeterminate = "StowIndeterminate";
    public const string CodeStowNotObserved = "StowNotObserved";
    public const string CodeStowTimedOut = "StowTimedOut";
    public const string CodeStowCancelled = "StowCancelled";
    public const string CodeStowBaselineUnavailable = "StowBaselineUnavailable";
    public const string CodeStowBagsUnavailable = "StowBagsUnavailable";
    public const string CodeStowNotCarried = "StowNotCarried";
    /// <summary>Stow line code: delivered units the bags no longer hold, so they were not asked for.</summary>
    public const string LineCodeNotCarried = "NotCarried";

    /// <summary>A counted-stock retrieval not settled this long after its persisted submission is cancelled.</summary>
    public static readonly TimeSpan RetrievalDeadline = TimeSpan.FromMinutes(10);
    /// <summary>A delivered stow not settled this long after its persisted submission is cancelled.</summary>
    public static readonly TimeSpan StowDeadline = TimeSpan.FromMinutes(15);

    private const string RetainersContainer = "retainers";
    private const string BagsContainer = "bags";
    private static readonly TimeSpan CustodyPollInterval = TimeSpan.FromSeconds(2);

    private DateTimeOffset? collectBagWaitStartedUtc;
    private DateTimeOffset? stowBagWaitStartedUtc;
    private DateTimeOffset nextCustodyPollUtc;
    private int custodySubmitAttempts;

    private enum CourierLeg
    {
        Fund,
        Haul,
        Collect,
    }

    // ---- Courier legs -----------------------------------------------------------------------

    private static CourierLeg? LegOf(CrossRegionAcquisitionPhase phase) => phase switch
    {
        CrossRegionAcquisitionPhase.FundingShopper => CourierLeg.Fund,
        CrossRegionAcquisitionPhase.HaulingToPrimary => CourierLeg.Haul,
        CrossRegionAcquisitionPhase.CollectingCountedStock => CourierLeg.Collect,
        _ => null,
    };

    /// <summary>The courier leg an interrupted session holds custody in, or null when it holds none.</summary>
    private CourierLeg? InterruptedCourierLeg() => session.InterruptedPhase switch
    {
        CrossRegionAcquisitionPhase.CollectingCountedStock =>
            CurrentCollection()?.State == CrossRegionCollectionState.Shipping ? CourierLeg.Collect : null,
        { } phase => LegOf(phase),
        null => null,
    };

    private Guid? RunIdOf(CourierLeg leg) => leg switch
    {
        CourierLeg.Fund => session.FundRunId,
        CourierLeg.Haul => session.HaulRunId,
        _ => CurrentCollection()?.RunId,
    };

    private void SetRunId(CourierLeg leg, Guid id)
    {
        switch (leg)
        {
            case CourierLeg.Fund:
                session.FundRunId = id;
                break;
            case CourierLeg.Haul:
                session.HaulRunId = id;
                break;
            default:
                if (CurrentCollection() is { } collection)
                    collection.RunId = id;
                break;
        }
    }

    private string? RequestIdOf(CourierLeg leg) => leg switch
    {
        CourierLeg.Fund => session.FundRequestId,
        CourierLeg.Haul => session.HaulRequestId,
        _ => CurrentCollection()?.RequestId,
    };

    private static string LegNoun(CourierLeg leg) => leg switch
    {
        CourierLeg.Fund => "funding",
        CourierLeg.Haul => "the return haul",
        _ => "the collection",
    };

    private static ImmutableArray<QuartermasterCourierItem> ToCourierItems(IEnumerable<CrossRegionManifestLine> lines) =>
        lines.Select(line => new QuartermasterCourierItem(line.ItemId, line.ItemName, line.IsHq, (int)Math.Min(line.Quantity, int.MaxValue)))
            .ToImmutableArray();

    // ---- Planning ---------------------------------------------------------------------------

    /// <summary>
    /// Splits the plan's counted stock by source. The primary's own stock is ignored; the shopper's rides home with
    /// the haul; stock on the courier carrier cannot be a courier source and is reported, not attempted.
    /// </summary>
    private void PlanCountedStock(QuartermasterCourierCarrier? carrier)
    {
        if (session.RequestDocumentSnapshot is not { } document)
            return;
        session.CountedStockPlanned = true;
        var bySource = new List<(CrossRegionCharacter Source, Dictionary<(uint ItemId, string HqPolicy), CrossRegionCountedLine> Lines)>();
        foreach (var line in document.Lines)
        {
            foreach (var counted in line.CountedStock ?? [])
            {
                if (counted.LocalContentId == 0 || counted.HomeWorldId == 0 || counted.Quantity == 0)
                    continue;
                var source = new CrossRegionCharacter(counted.LocalContentId, counted.HomeWorldId, counted.CharacterName, counted.HomeWorldName);
                if (source.Matches(session.Primary))
                    continue;
                var inRetainers = string.Equals(counted.Container, RetainersContainer, StringComparison.OrdinalIgnoreCase);
                if (!inRetainers && !string.Equals(counted.Container, BagsContainer, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Counted stock for {line.ItemName} on {source.Label} names unknown container '{counted.Container}'; it is not collected.", null);
                    continue;
                }
                if (carrier is { Configured: true } && carrier.LocalContentId == source.LocalContentId && carrier.HomeWorldId == source.HomeWorldId)
                {
                    session.NotCollected.Add(new(line.ItemId, line.ItemName, source.Label, counted.Quantity, CodeCountedStockOnCarrier));
                    log($"{CodeCountedStockOnCarrier}: {counted.Quantity} {line.ItemName} counted on {source.Label} ({counted.Container}) stays put; " +
                        "the courier carrier cannot also be a transfer source.", null);
                    continue;
                }
                var entry = bySource.FindIndex(candidate => candidate.Source.Matches(source));
                if (entry < 0)
                {
                    bySource.Add((source, new()));
                    entry = bySource.Count - 1;
                }
                var lines = bySource[entry].Lines;
                var key = (line.ItemId, line.HqPolicy);
                var current = lines.GetValueOrDefault(key) ?? new CrossRegionCountedLine(line.ItemId, line.ItemName, line.HqPolicy, 0, 0);
                lines[key] = inRetainers
                    ? current with { RetainerQuantity = checked(current.RetainerQuantity + counted.Quantity) }
                    : current with { BagQuantity = checked(current.BagQuantity + counted.Quantity) };
            }
        }
        foreach (var (source, lines) in bySource)
        {
            var collection = new CrossRegionCollection { Source = source, Lines = lines.Values.ToList() };
            if (source.Matches(session.Shopper))
                session.ShopperCounted = collection;
            else
                session.Collections.Add(collection);
        }
    }

    private bool HasShopperCountedStock => session.ShopperCounted is { Lines.Count: > 0 };

    private CrossRegionCollection? CurrentCollection() => session.Collections.FirstOrDefault(collection => !collection.IsSettled);

    private string CollectingMessage() => CurrentCollection() is { } collection
        ? $"Collecting counted stock from {collection.Source.Label}."
        : $"Returning to {session.Primary?.Label}.";

    // ---- After delivery ---------------------------------------------------------------------

    /// <summary>The haul (or the no-haul return) is done and the primary is logged in: collect, then stow, then finish.</summary>
    private void AfterDelivery(string summary)
    {
        if (session.Phase == CrossRegionAcquisitionPhase.HaulingToPrimary)
        {
            session.HaulDelivered = true;
            if (session.ShopperCounted is { State: CrossRegionCollectionState.Shipping } shopperCounted)
                shopperCounted.State = CrossRegionCollectionState.Completed;
        }
        session.DeliverySummary ??= summary;
        if (CurrentCollection() is not null)
        {
            ResetCustodyTransients();
            SetPhase(CrossRegionAcquisitionPhase.CollectingCountedStock, CollectingMessage());
            return;
        }
        AfterCollections();
    }

    private void AfterCollections()
    {
        if (session.RequestDocumentSnapshot?.StowAfterDelivery == true && session.Stow is not { Settled: true })
        {
            ResetCustodyTransients();
            SetPhase(CrossRegionAcquisitionPhase.StowingDelivered, "Stowing delivered items.");
            return;
        }
        Complete(FinalSummary());
    }

    private void ResetCustodyTransients()
    {
        session.CharacterSwitchAttempts = 0;
        lastSwitchRequestUtc = null;
        collectBagWaitStartedUtc = null;
        stowBagWaitStartedUtc = null;
        awaitingRunAppearanceSinceUtc = null;
        submitAttempts = 0;
        custodySubmitAttempts = 0;
        nextCustodyPollUtc = DateTimeOffset.MinValue;
    }

    // ---- Collection -------------------------------------------------------------------------

    private void TickCollecting(DateTimeOffset now)
    {
        var collection = CurrentCollection();
        if (collection is null)
        {
            // Every source settled; the run ends on the primary.
            var primary = session.Primary!;
            if (!primary.Matches(game.LoggedInCharacter))
            {
                DriveCharacterSwitch(primary, CrossRegionAcquisitionPhase.CollectingCountedStock, CodeCollectReturnSwitchFailed, now);
                return;
            }
            ResetCustodyTransients();
            AfterCollections();
            return;
        }
        if (collection.State == CrossRegionCollectionState.Shipping)
        {
            TickCourier(CourierLeg.Collect, now);
            return;
        }
        if (!collection.Source.Matches(game.LoggedInCharacter))
        {
            // A source that cannot be reached is not collected; the run moves on to the rest and the stow.
            DriveCharacterSwitch(collection.Source, CrossRegionAcquisitionPhase.CollectingCountedStock, CodeCollectSourceSwitchFailed, now,
                onExhausted: detail =>
                {
                    SettleNotCollected(collection, CodeCollectSourceSwitchFailed, detail);
                    ResetCustodyTransients();
                });
            return;
        }
        if (session.CharacterSwitchAttempts > 0 || lastSwitchRequestUtc is not null)
        {
            session.CharacterSwitchAttempts = 0;
            lastSwitchRequestUtc = null;
        }
        var index = session.Collections.IndexOf(collection);
        if (!collection.RetrievalSettled)
        {
            if (collection.State == CrossRegionCollectionState.Pending)
            {
                collection.State = CrossRegionCollectionState.Retrieving;
                Save();
            }
            TickRetrieval(collection, index, now);
            return;
        }
        if (ReadSourceBags(collection, now) is not { } bags)
            return;
        var (manifest, outcomes) = Allocate(collection, CarriedByVariant(bags));
        collection.Outcomes = outcomes;
        if (manifest.Count == 0)
        {
            SettleNotCollected(collection, CodeCountedStockNotFound,
                $"None of the counted stock is in {collection.Source.CharacterName}'s bags.");
            return;
        }
        // I1: the collect request id and manifest are durable before the courier sees them.
        collection.Attempt++;
        collection.RequestId = CrossRegionAcquisitionSession.CollectRequestIdFor(session.SessionId, index, collection.Attempt);
        collection.RunId = null;
        collection.Manifest = manifest;
        collection.State = CrossRegionCollectionState.Shipping;
        submitAttempts = 0;
        awaitingRunAppearanceSinceUtc = null;
        SetPhase(CrossRegionAcquisitionPhase.CollectingCountedStock,
            $"Collecting {manifest.Sum(line => (long)line.Quantity):N0} item(s) from {collection.Source.Label}.");
        SubmitCourier(CourierLeg.Collect);
    }

    private IReadOnlyList<CrossRegionBagStack>? ReadSourceBags(CrossRegionCollection collection, DateTimeOffset now)
    {
        var bags = game.ReadCarriedBags();
        if (bags is not null)
        {
            collectBagWaitStartedUtc = null;
            return bags;
        }
        collectBagWaitStartedUtc ??= now;
        if (now - collectBagWaitStartedUtc.Value >= BagReadTimeout)
        {
            collectBagWaitStartedUtc = null;
            if (collection == session.ShopperCounted)
                Fail(CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul, "BagsUnavailable",
                    $"Could not read {collection.Source.CharacterName}'s bags within {BagReadTimeout.TotalSeconds:N0}s.", canResumeCourier: false);
            else
                SettleNotCollected(collection, CodeCollectBagsUnavailable,
                    $"Could not read {collection.Source.CharacterName}'s bags within {BagReadTimeout.TotalSeconds:N0}s.");
        }
        else
            session.LastMessage = $"Waiting for {collection.Source.CharacterName}'s bags to load.";
        return null;
    }

    private void SettleNotCollected(CrossRegionCollection collection, string code, string detail)
    {
        collection.State = CrossRegionCollectionState.NotCollected;
        collection.OutcomeCode = code;
        collection.Detail = detail;
        log($"{code}: counted stock on {collection.Source.Label} is not collected. {detail}", null);
        session.LastMessage = $"{collection.Source.Label}: not collected. {detail}";
        collectBagWaitStartedUtc = null;
        Save();
    }

    private void OnCollectShipped()
    {
        if (CurrentCollection() is not { } collection)
            return;
        collection.State = CrossRegionCollectionState.Completed;
        session.CourierPhaseSummary = null;
        ResetCustodyTransients();
        session.LastMessage = $"Collected {collection.Manifest.Sum(line => (long)line.Quantity):N0} item(s) from {collection.Source.Label}.";
        Save();
    }

    /// <summary>
    /// A collect courier run failed with nothing in custody: no item or gil of this source is unaccounted for, so
    /// the source is not collected and the run moves on. Units it may have delivered are not claimed for the stow.
    /// </summary>
    private void OnCollectFailedWithoutCustody(QuartermasterCourierRun run)
    {
        if (CurrentCollection() is not { } collection)
            return;
        session.CourierPhaseSummary = null;
        SettleNotCollected(collection, CodeCollectRunFailed,
            $"Courier collection {run.State.ToLowerInvariant()} with nothing in custody ({run.Code ?? run.State}): {run.Detail ?? "no detail"}");
        ResetCustodyTransients();
    }

    private void OnCollectRejected(string code, string message)
    {
        if (CurrentCollection() is not { } collection)
            return;
        SettleNotCollected(collection, CodeCollectRejected, $"Quartermaster rejected the transfer ({code}): {message}");
        ResetCustodyTransients();
    }

    /// <summary>
    /// Caps each counted line by what is observed in the source's bags now: the counted bag quantity, plus the
    /// counted retainer quantity once a retrieval ran. Either takes NQ first, then HQ. A shortfall is reported, not retried.
    /// </summary>
    private (List<CrossRegionManifestLine> Manifest, List<CrossRegionLineOutcome> Outcomes) Allocate(
        CrossRegionCollection collection,
        Dictionary<(uint ItemId, bool IsHq), ulong> available)
    {
        var taken = new Dictionary<(uint ItemId, bool IsHq), (string Name, ulong Quantity)>();
        var outcomes = new List<CrossRegionLineOutcome>();
        foreach (var line in collection.Lines)
        {
            ulong cap = line.BagQuantity + (collection.RetrievalAttempted ? line.RetainerQuantity : 0u);
            if (cap == 0)
                continue;
            bool[] qualities = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy) switch
            {
                "HqOnly" => [true],
                "NqOnly" => [false],
                _ => [false, true],
            };
            var remaining = cap;
            foreach (var hq in qualities)
            {
                var key = (line.ItemId, hq);
                var take = Math.Min(remaining, available.GetValueOrDefault(key));
                if (take == 0)
                    continue;
                available[key] -= take;
                remaining -= take;
                var previous = taken.GetValueOrDefault(key);
                taken[key] = (line.ItemName, previous.Quantity + take);
            }
            if (remaining > 0)
            {
                var code = collection.RetrievalOutcomeCode is not null && line.RetainerQuantity > 0 && collection.RetrievalAttempted
                    ? collection.RetrievalOutcomeCode
                    : CodeCountedShort;
                outcomes.Add(new(line.ItemId, line.ItemName, collection.Source.Label, (uint)Math.Min(remaining, uint.MaxValue), code));
                log($"{code}: {collection.Source.Label} holds {cap - remaining} of {cap} counted {line.ItemName} ({line.HqPolicy}) in bags; " +
                    $"{remaining} not collected.", null);
            }
        }
        var manifest = taken
            .Where(entry => entry.Value.Quantity > 0)
            .Select(entry => new CrossRegionManifestLine(entry.Key.ItemId, entry.Value.Name, entry.Key.IsHq, (uint)Math.Min(entry.Value.Quantity, uint.MaxValue)))
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToList();
        return (manifest, outcomes);
    }

    private static Dictionary<(uint ItemId, bool IsHq), ulong> CarriedByVariant(IEnumerable<CrossRegionBagStack> bags) =>
        bags.GroupBy(stack => (stack.ItemId, stack.IsHq))
            .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, stack) => total + stack.Quantity));

    /// <summary>
    /// Adds the shopper's counted stock to the reconciled purchases. Counted units come only from what remains
    /// in the bags after the purchases, so nothing is counted twice.
    /// </summary>
    private IReadOnlyList<CrossRegionManifestLine> AddShopperCountedStock(
        IReadOnlyList<CrossRegionManifestLine> purchased,
        IReadOnlyList<CrossRegionBagStack> bags)
    {
        if (session.ShopperCounted is not { Lines.Count: > 0 } shopperCounted)
            return purchased;
        var available = CarriedByVariant(bags);
        foreach (var line in purchased)
        {
            var key = (line.ItemId, line.IsHq);
            available[key] = available.GetValueOrDefault(key) - Math.Min(available.GetValueOrDefault(key), line.Quantity);
        }
        var (counted, outcomes) = Allocate(shopperCounted, available);
        shopperCounted.Outcomes = outcomes;
        shopperCounted.Manifest = counted;
        shopperCounted.State = counted.Count > 0 ? CrossRegionCollectionState.Shipping : CrossRegionCollectionState.NotCollected;
        shopperCounted.OutcomeCode = counted.Count > 0 ? null : CodeCountedStockNotFound;
        return purchased.Concat(counted)
            .GroupBy(line => (line.ItemId, line.IsHq))
            .Select(group => new CrossRegionManifestLine(group.Key.ItemId, group.First().ItemName, group.Key.IsHq,
                (uint)Math.Min(group.Aggregate(0UL, (total, line) => total + line.Quantity), uint.MaxValue),
                group.Aggregate(0UL, (total, line) => total + line.SpentGil)))
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToArray();
    }

    // ---- Retainer retrieval on the source ----------------------------------------------------

    /// <summary>
    /// Chains Quartermaster's batched retrieval on the logged-in source for counted retainer stock. The request
    /// id, operation id, submission time and items are persisted before submission so a replay is idempotent;
    /// a settled retrieval is never resubmitted.
    /// </summary>
    private void TickRetrieval(CrossRegionCollection collection, int index, DateTimeOffset now)
    {
        var retainerLines = collection.Lines.Where(line => line.RetainerQuantity > 0).ToList();
        if (retainerLines.Count == 0)
        {
            SettleRetrieval(collection, attempted: false, code: null, detail: null);
            return;
        }
        if (collection.RetrievalRequestId is null)
        {
            if (custody is null || !custody.HasAutomaticRetrieval)
            {
                SettleRetrieval(collection, attempted: false, CodeCountedRetrievalUnavailable,
                    "Quartermaster does not advertise automatic retrieval.");
                return;
            }
            if (ReadSourceBags(collection, now) is not { } bags)
                return;
            var carried = bags.GroupBy(stack => stack.ItemId).ToDictionary(group => group.Key, group => group.Aggregate(0L, (total, stack) => total + stack.Quantity));
            collection.RetrievalItems = retainerLines
                .GroupBy(line => line.ItemId)
                .Select(group =>
                {
                    var shortage = (int)Math.Min(group.Aggregate(0L, (total, line) => total + line.RetainerQuantity), int.MaxValue);
                    var target = (int)Math.Min(carried.GetValueOrDefault(group.Key) + shortage, int.MaxValue);
                    return new CrossRegionRetrievalItem(group.Key, group.First().ItemName, target, shortage);
                })
                .ToList();
            collection.RetrievalRequestId = CrossRegionAcquisitionSession.RetrievalRequestIdFor(session.SessionId, index);
            collection.RetrievalOperationId = collection.RetrievalRequestId + "-op";
            collection.RetrievalSubmittedAtUtc = now;
            custodySubmitAttempts = 0;
            session.LastMessage = $"Retrieving counted stock from {collection.Source.CharacterName}'s retainers.";
            Save();
        }
        if (collection.RetrievalSubmittedAtUtc is null)
        {
            // Sessions persisted before submit times existed: the deadline starts now.
            collection.RetrievalSubmittedAtUtc = now;
            Save();
        }
        if (now < nextCustodyPollUtc)
            return;
        nextCustodyPollUtc = now + CustodyPollInterval;
        var overdue = now - collection.RetrievalSubmittedAtUtc.Value >= RetrievalDeadline;
        if (custody is null)
        {
            if (overdue)
                TimeOutRetrieval(collection, "Quartermaster is unavailable");
            else
                session.LastMessage = "Waiting for Quartermaster to report the retainer retrieval.";
            return;
        }
        var owner = new QuartermasterOwnerScope(collection.Source.LocalContentId, collection.Source.HomeWorldId,
            collection.Source.CharacterName, collection.Source.HomeWorldName);
        if (custody.TryGetOperation(collection.RetrievalOperationId!, owner, out var operation, out var error) &&
            operation is not null && !string.Equals(operation.Status, "not_found", StringComparison.OrdinalIgnoreCase))
        {
            if (!operation.IsTerminal)
            {
                if (overdue)
                    TimeOutRetrieval(collection, $"last status {operation.Status}");
                else
                    session.LastMessage = $"Retrieving counted stock from {collection.Source.CharacterName}'s retainers: {operation.Status}.";
                return;
            }
            if (operation.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || operation.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                SettleRetrieval(collection, attempted: true, code: null, detail: null);
            else if (operation.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
                SettleRetrieval(collection, attempted: false, CodeCountedRetrievalRejected, operation.Message ?? operation.Code ?? operation.Status);
            else if (operation.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                SettleRetrieval(collection, attempted: true, CodeCountedRetrievalCancelled, $"{operation.Code ?? operation.Status}: {operation.Message}");
            else
                SettleRetrieval(collection, attempted: true, CodeCountedRetrievalIncomplete, $"{operation.Status}: {operation.Message ?? operation.Code}");
            return;
        }
        if (overdue)
        {
            TimeOutRetrieval(collection, $"Quartermaster does not report it ({error})");
            return;
        }
        // Not known to Quartermaster yet: (re)submit under the persisted identity. A replay returns the existing operation.
        var request = new QuartermasterShortageRequest(
            collection.RetrievalRequestId!,
            collection.RetrievalOperationId!,
            collection.RetrievalSubmittedAtUtc ?? now,
            new QuartermasterOwner(collection.Source.LocalContentId, collection.Source.HomeWorldId, collection.Source.CharacterName, collection.Source.HomeWorldName),
            ExecuteImmediately: true,
            collection.RetrievalItems.Select(item => new QuartermasterShortageTarget(item.ItemId, item.ItemName, item.TargetQuantity, item.ShortageQuantity)).ToImmutableArray());
        if (!custody.TrySubmitShortages(request, out var ack, out error))
        {
            custodySubmitAttempts++;
            if (custodySubmitAttempts >= MaxSubmitAttempts)
                SettleRetrieval(collection, attempted: false, CodeCountedRetrievalSubmitFailed,
                    $"Retrieval submission did not succeed after {MaxSubmitAttempts} attempts: {error}");
            else
                session.LastMessage = $"Retrieval submission pending ({custodySubmitAttempts}/{MaxSubmitAttempts}): {error}";
            return;
        }
        custodySubmitAttempts = 0;
        if (!ack!.Accepted)
        {
            SettleRetrieval(collection, attempted: false, CodeCountedRetrievalRejected, ack.Message ?? ack.Status);
            return;
        }
        session.LastMessage = $"Retrieving counted stock from {collection.Source.CharacterName}'s retainers.";
    }

    /// <summary>
    /// The retrieval missed its deadline: withdraw it from Quartermaster and settle as attempted, so the source
    /// (or the shopper's haul) proceeds with whatever reached the bags, capped by the counted quantity.
    /// </summary>
    private void TimeOutRetrieval(CrossRegionCollection collection, string why)
    {
        CancelCustodyRequest(collection.RetrievalRequestId!, $"MarketMafioso retrieval deadline of {RetrievalDeadline.TotalMinutes:N0} minutes passed.");
        SettleRetrieval(collection, attempted: true, CodeCountedRetrievalTimedOut,
            $"Retrieval {collection.RetrievalRequestId} did not finish within the {RetrievalDeadline.TotalMinutes:N0}-minute retrieval deadline " +
            $"({why}); cancelled in Quartermaster and continuing with what is in the bags.");
    }

    private void SettleRetrieval(CrossRegionCollection collection, bool attempted, string? code, string? detail)
    {
        collection.RetrievalSettled = true;
        collection.RetrievalAttempted = attempted;
        collection.RetrievalOutcomeCode = code;
        if (code is not null)
        {
            var units = collection.Lines.Sum(line => (long)line.RetainerQuantity);
            log($"{code}: {units} counted retainer unit(s) on {collection.Source.Label}" +
                (attempted ? " were only partly retrieved" : " are not collected") + $". {detail}", null);
        }
        nextCustodyPollUtc = DateTimeOffset.MinValue;
        collectBagWaitStartedUtc = null;
        Save();
    }

    // ---- Delivered stow ---------------------------------------------------------------------

    private void TickStowing(DateTimeOffset now)
    {
        var stow = session.Stow ??= new CrossRegionStow();
        if (stow.Settled)
        {
            Complete(FinalSummary());
            return;
        }
        var primary = session.Primary!;
        if (stow.RequestId is null)
        {
            var claimed = DeliveredItems();
            if (claimed.Count == 0)
            {
                stow.Settled = true;
                Complete(FinalSummary());
                return;
            }
            stow.Claimed = claimed;
            if (custody is null || !custody.HasDeliveredStow)
            {
                stow.Items = claimed;
                SettleStow(stow, CodeStowUnavailable, $"Quartermaster does not advertise {QuartermasterIpcClient.DeliveredStowCapability}; delivered items stay in the bags.");
                return;
            }
            if (session.PrimaryBagBaseline is not { } baseline)
            {
                SettleStow(stow, CodeStowBaselineUnavailable,
                    "The primary's bags were not recorded before delivery, so delivered units cannot be told from earlier stock; nothing is stowed.");
                return;
            }
            if (!primary.Matches(game.LoggedInCharacter))
            {
                DriveCharacterSwitch(primary, CrossRegionAcquisitionPhase.StowingDelivered, CodeStowPrimarySwitchFailed, now);
                return;
            }
            if (ReadPrimaryBags(stow, now) is not { } bags)
                return;
            var items = CapToDelivered(claimed, baseline, bags);
            if (items.Count == 0)
            {
                SettleStow(stow, CodeStowNotCarried, "None of the delivered units are still in the primary's bags beyond what they held at Start.");
                return;
            }
            // Identity, items and submit time are durable before Quartermaster sees the request.
            stow.Items = items;
            stow.RequestId = CrossRegionAcquisitionSession.StowRequestIdFor(session.SessionId);
            stow.SubmittedAtUtc = now;
            custodySubmitAttempts = 0;
            Save();
        }
        if (stow.SubmittedAtUtc is null)
        {
            // Sessions persisted before submit times existed: the deadline starts now.
            stow.SubmittedAtUtc = now;
            Save();
        }
        if (!primary.Matches(game.LoggedInCharacter))
        {
            DriveCharacterSwitch(primary, CrossRegionAcquisitionPhase.StowingDelivered, CodeStowPrimarySwitchFailed, now);
            return;
        }
        if (now < nextCustodyPollUtc)
            return;
        nextCustodyPollUtc = now + CustodyPollInterval;
        var overdue = now - stow.SubmittedAtUtc.Value >= StowDeadline;
        if (custody is null)
        {
            if (overdue)
                TimeOutStow(stow, "Quartermaster is unavailable");
            else
                session.LastMessage = "Waiting for Quartermaster to report the stow.";
            return;
        }
        if (!stow.Submitted)
        {
            if (overdue)
            {
                TimeOutStow(stow, "the request was never acknowledged");
                return;
            }
            var request = new QuartermasterDeliveredStowRequest(
                stow.RequestId!,
                new QuartermasterOwner(primary.LocalContentId, primary.HomeWorldId, primary.CharacterName, primary.HomeWorldName),
                stow.Items.Select(line => new QuartermasterDeliveredStowItem(line.ItemId, line.ItemName, line.IsHq, (int)Math.Min(line.Quantity, int.MaxValue))).ToArray());
            if (!custody.TrySubmitDeliveredStow(request, out var ack, out var error))
            {
                custodySubmitAttempts++;
                if (custodySubmitAttempts >= MaxSubmitAttempts)
                    SettleStow(stow, CodeStowSubmitFailed, $"Stow submission did not succeed after {MaxSubmitAttempts} attempts; acceptance is unknown: {error}");
                else
                    session.LastMessage = $"Stow submission pending ({custodySubmitAttempts}/{MaxSubmitAttempts}): {error}";
                return;
            }
            custodySubmitAttempts = 0;
            stow.Status = ack!.Status;
            stow.Lines = ack.Lines.ToList();
            if (ack.IsRejected)
            {
                SettleStow(stow, CodeStowRejected, $"{ack.ErrorCode ?? ack.Status}: {ack.Message}");
                return;
            }
            if (ack.IsNothingToStow)
            {
                stow.Lines = stow.Lines.Select(ClassifyStowLine).ToList();
                SettleStow(stow, StowShortfallCode(stow), ack.Message);
                return;
            }
            stow.Submitted = true;
            stow.OperationId = ack.OperationId;
            session.LastMessage = $"Stowing {stow.Lines.Sum(line => (long)line.Planned):N0} item(s) into home retainers.";
            Save();
            return;
        }
        if (!custody.TryGetDeliveredStow(stow.RequestId!, out var status, out var statusError))
        {
            if (overdue)
                TimeOutStow(stow, $"status unavailable: {statusError}");
            else
                session.LastMessage = $"Waiting for Quartermaster stow status: {statusError}";
            return;
        }
        if (status!.IsNotFound)
        {
            SettleStow(stow, CodeStowNotObserved, $"Quartermaster no longer reports stow {stow.RequestId}.");
            return;
        }
        MergeStowLines(stow, status);
        if (!status.IsTerminal)
        {
            if (overdue)
                TimeOutStow(stow, $"last status {status.Status}");
            else
                session.LastMessage = $"Stowing delivered items: {status.Status}.";
            return;
        }
        stow.Status = status.Status;
        stow.Lines = stow.Lines.Select(ClassifyStowLine).ToList();
        var code = status.Status.ToLowerInvariant() switch
        {
            "succeeded" or "partially_succeeded" => StowShortfallCode(stow),
            "cancelled" => CodeStowCancelled,
            "indeterminate" => CodeStowIndeterminate,
            "rejected" => CodeStowRejected,
            _ => CodeStowFailed,
        };
        SettleStow(stow, code, status.Message);
    }

    /// <summary>Keeps the acknowledged request, plan and carried counts; takes stowed counts and destinations from the status.</summary>
    private static void MergeStowLines(CrossRegionStow stow, QuartermasterDeliveredStowStatus status)
    {
        var observed = status.Lines
            .GroupBy(line => (line.ItemId, line.IsHq))
            .ToDictionary(group => group.Key, group => group.First());
        stow.Lines = stow.Lines
            .Select(line => observed.TryGetValue((line.ItemId, line.IsHq), out var current)
                ? line with
                {
                    Stowed = current.Stowed,
                    Code = current.Code,
                    Destinations = current.Destinations.Count > 0 ? current.Destinations : line.Destinations,
                }
                : line)
            .ToList();
    }

    /// <summary>
    /// A settled line's reason from its counts, so a partly routed line is never reported as plain Stowed: NoHome
    /// stays; otherwise everything stowed = Stowed, short of the plan = PartlyStowed/NotStowed, plan short of what
    /// was carried = NoRoom, and carried short of the request = NotCarried.
    /// </summary>
    private static QuartermasterDeliveredStowLine ClassifyStowLine(QuartermasterDeliveredStowLine line)
    {
        if (line.Code == "NoHome")
            return line;
        var carried = Math.Min(line.Carried ?? line.Requested, line.Requested);
        var code = line.Stowed >= line.Requested ? "Stowed"
            : line.Stowed < line.Planned ? line.Stowed > 0 ? "PartlyStowed" : "NotStowed"
            : line.Planned < carried ? "NoRoom"
            : carried < line.Requested ? LineCodeNotCarried
            : line.Code;
        return line with { Code = code };
    }

    /// <summary>StowNoRoom when every shortfall is lack of room, StowPartial for any other shortfall, null when none.</summary>
    private static string? StowShortfallCode(CrossRegionStow stow)
    {
        var shortfalls = stow.Lines.Where(line => line.Code != "NoHome" && line.Stowed < line.Requested).ToList();
        return shortfalls.Count == 0 ? null : shortfalls.All(line => line.Code == "NoRoom") ? CodeStowNoRoom : CodeStowPartial;
    }

    private void TimeOutStow(CrossRegionStow stow, string why)
    {
        CancelCustodyRequest(stow.RequestId!, $"MarketMafioso stow deadline of {StowDeadline.TotalMinutes:N0} minutes passed.");
        SettleStow(stow, CodeStowTimedOut,
            $"Stow {stow.RequestId} did not finish within the {StowDeadline.TotalMinutes:N0}-minute stow deadline ({why}); " +
            "cancelled in Quartermaster. What was not stowed stays in the bags.");
    }

    private IReadOnlyList<CrossRegionBagStack>? ReadPrimaryBags(CrossRegionStow stow, DateTimeOffset now)
    {
        var bags = game.ReadCarriedBags();
        if (bags is not null)
        {
            stowBagWaitStartedUtc = null;
            return bags;
        }
        stowBagWaitStartedUtc ??= now;
        if (now - stowBagWaitStartedUtc.Value >= BagReadTimeout)
        {
            stowBagWaitStartedUtc = null;
            SettleStow(stow, CodeStowBagsUnavailable,
                $"Could not read {session.Primary?.CharacterName}'s bags within the {BagReadTimeout.TotalSeconds:N0}s bag-read limit; nothing is stowed.");
        }
        else
            session.LastMessage = "Waiting for bags to load before stowing.";
        return null;
    }

    /// <summary>
    /// Per item variant: min(delivered by this run, carried now − carried at Start). Units the primary held before
    /// the run are never stowed; a delivered unit no longer in the bags is reported as not carried.
    /// </summary>
    private List<CrossRegionManifestLine> CapToDelivered(
        IReadOnlyList<CrossRegionManifestLine> claimed,
        IReadOnlyList<CrossRegionManifestLine> baseline,
        IReadOnlyList<CrossRegionBagStack> bags)
    {
        var before = baseline.GroupBy(line => (line.ItemId, line.IsHq))
            .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, line) => total + line.Quantity));
        var carried = CarriedByVariant(bags);
        var items = new List<CrossRegionManifestLine>();
        foreach (var line in claimed)
        {
            var key = (line.ItemId, line.IsHq);
            var now = carried.GetValueOrDefault(key);
            var gained = now - Math.Min(now, before.GetValueOrDefault(key));
            var quantity = Math.Min(line.Quantity, gained);
            if (quantity < line.Quantity)
                log($"{LineCodeNotCarried}: {line.Quantity - quantity} of {line.Quantity} delivered {line.ItemName}{(line.IsHq ? " HQ" : string.Empty)} " +
                    $"are not stowed; the bags hold {now} against {before.GetValueOrDefault(key)} at Start.", null);
            if (quantity > 0)
                items.Add(line with { Quantity = (uint)quantity });
        }
        return items;
    }

    /// <summary>The primary's carried bags per item variant, for the document's items only.</summary>
    private static List<CrossRegionManifestLine>? PrimaryBagSnapshot(IReadOnlyList<CrossRegionBagStack>? bags, MarketAcquisitionRequestDocument document)
    {
        if (bags is null)
            return null;
        var wanted = document.Lines.Select(line => line.ItemId).ToHashSet();
        return bags.Where(stack => wanted.Contains(stack.ItemId))
            .GroupBy(stack => (stack.ItemId, stack.IsHq))
            .Select(group => new CrossRegionManifestLine(group.Key.ItemId, string.Empty, group.Key.IsHq,
                (uint)Math.Min(group.Aggregate(0UL, (total, stack) => total + stack.Quantity), uint.MaxValue)))
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToList();
    }

    private void SettleStow(CrossRegionStow stow, string? code, string? detail)
    {
        stow.Settled = true;
        stow.OutcomeCode = code;
        stow.Detail = detail;
        stowBagWaitStartedUtc = null;
        if (code is not null)
            log($"{code}: delivered stow {stow.RequestId ?? "(not submitted)"} for {stow.Items.Sum(line => (long)line.Quantity)} unit(s). {detail}", null);
        Complete(FinalSummary());
    }

    // ---- Cancelling open Quartermaster work ------------------------------------------------------

    /// <summary>
    /// Withdraws every retrieval and stow this run submitted that has not settled: a not-started operation is
    /// cancelled, a running one stops after its current retainer. Quartermaster also expires a requested
    /// operation that never starts, so an unreachable Quartermaster here cannot leave one to run much later.
    /// </summary>
    private void CancelOpenCustodyOperations(string reason)
    {
        var open = (session.ShopperCounted is { } shopper ? new[] { shopper } : Array.Empty<CrossRegionCollection>())
            .Concat(session.Collections)
            .Where(collection => collection.RetrievalRequestId is not null && !collection.RetrievalSettled)
            .Select(collection => collection.RetrievalRequestId!)
            .Concat(session.Stow is { RequestId: { } stowRequest, Settled: false } ? new[] { stowRequest } : [])
            .ToList();
        foreach (var requestId in open)
            CancelCustodyRequest(requestId, reason);
    }

    private void CancelCustodyRequest(string requestId, string reason)
    {
        if (custody is null)
        {
            log($"Quartermaster request {requestId} could not be cancelled: Quartermaster is unavailable. It expires there if it never starts.", null);
            return;
        }
        if (custody.TryCancelRequestedOperation(requestId, reason, out var ack, out var error))
            log($"Quartermaster request {requestId}: {ack!.Status}. {ack.Message}", null);
        else
            log($"Quartermaster request {requestId} could not be cancelled: {error} It expires there if it never starts.", null);
    }

    /// <summary>What this run put into the primary's bags: the delivered haul plus every completed collection.</summary>
    private List<CrossRegionManifestLine> DeliveredItems() =>
        (session.HaulDelivered ? session.HaulManifest : [])
            .Concat(session.Collections.Where(collection => collection.State == CrossRegionCollectionState.Completed).SelectMany(collection => collection.Manifest))
            .GroupBy(line => (line.ItemId, line.IsHq))
            .Select(group => new CrossRegionManifestLine(group.Key.ItemId, group.First().ItemName, group.Key.IsHq,
                (uint)Math.Min(group.Aggregate(0UL, (total, line) => total + line.Quantity), uint.MaxValue)))
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.IsHq)
            .ToList();

    // ---- Results ----------------------------------------------------------------------------

    private string FinalSummary()
    {
        var parts = new List<string> { session.DeliverySummary ?? "Done." };
        var collected = CollectedUnits();
        if (collected > 0)
        {
            var sources = session.Collections.Count(collection => collection.State == CrossRegionCollectionState.Completed) +
                          (session.ShopperCounted is { State: CrossRegionCollectionState.Completed } ? 1 : 0);
            parts.Add($"Collected {collected:N0} counted item(s) from {sources:N0} character(s).");
        }
        if (session.Stow is { Settled: true } stow)
        {
            var stowed = stow.Lines.Sum(line => (long)line.Stowed);
            if (stowed > 0)
                parts.Add($"Stowed {stowed:N0} into home retainers.");
            if (stow.OutcomeCode == CodeStowUnavailable)
                parts.Add("Not stowed: Quartermaster has no delivered stow.");
            else if (stow.OutcomeCode is CodeStowRejected or CodeStowSubmitFailed or CodeStowFailed or CodeStowIndeterminate or CodeStowNotObserved or
                     CodeStowTimedOut or CodeStowCancelled or CodeStowBaselineUnavailable or CodeStowBagsUnavailable or CodeStowNotCarried)
                parts.Add($"Stow {stow.OutcomeCode}.");
        }
        return string.Join(" ", parts);
    }

    private static long Sum(IEnumerable<CrossRegionManifestLine> lines, uint itemId) =>
        lines.Where(line => line.ItemId == itemId).Sum(line => (long)line.Quantity);

    private long CollectedUnits() =>
        session.Collections.Where(collection => collection.State == CrossRegionCollectionState.Completed)
            .Concat(session.ShopperCounted is { State: CrossRegionCollectionState.Completed } shopper ? new[] { shopper } : Array.Empty<CrossRegionCollection>())
            .Sum(collection => collection.Manifest.Sum(line => (long)line.Quantity));

    /// <summary>
    /// Per plan line: bought, counted stock held elsewhere, collected and stowed, listed only where collection or
    /// stow fell short. Items without a home are not a shortfall.
    /// </summary>
    public IReadOnlyList<CrossRegionLineResult> LineResults()
    {
        if (!session.CountedStockPlanned && session.Stow is null)
            return [];
        var all = session.Collections
            .Concat(session.ShopperCounted is { } shopper ? new[] { shopper } : Array.Empty<CrossRegionCollection>())
            .ToList();
        var itemIds = session.PurchasedManifest.Select(line => line.ItemId)
            .Concat(all.SelectMany(collection => collection.Lines).Select(line => line.ItemId))
            .Concat(session.NotCollected.Select(outcome => outcome.ItemId))
            .Concat(session.Stow?.Lines.Select(line => line.ItemId) ?? [])
            .Distinct()
            .ToList();
        var results = new List<CrossRegionLineResult>();
        foreach (var itemId in itemIds)
        {
            var name = session.RequestDocumentSnapshot?.Lines.FirstOrDefault(line => line.ItemId == itemId)?.ItemName
                       ?? session.PurchasedManifest.FirstOrDefault(line => line.ItemId == itemId)?.ItemName
                       ?? itemId.ToString();
            var bought = (uint)session.PurchasedManifest.Where(line => line.ItemId == itemId).Sum(line => (long)line.Quantity);
            var counted = (uint)(all.SelectMany(collection => collection.Lines).Where(line => line.ItemId == itemId).Sum(line => (long)line.Total) +
                                 session.NotCollected.Where(outcome => outcome.ItemId == itemId).Sum(outcome => (long)outcome.Quantity));
            var collected = (uint)all.Where(collection => collection.State == CrossRegionCollectionState.Completed)
                .SelectMany(collection => collection.Manifest).Where(line => line.ItemId == itemId).Sum(line => (long)line.Quantity);
            var allStowLines = session.Stow?.Lines.Where(line => line.ItemId == itemId).ToList() ?? [];
            var noHome = allStowLines.Count > 0 && allStowLines.All(line => line.Code == "NoHome");
            var stowLines = allStowLines.Where(line => line.Code != "NoHome").ToList();
            // Delivered units the bags no longer held were never asked for; they still count against the stow.
            var notCarried = noHome || session.Stow is not { } stow ? 0L
                : Math.Max(0L, Sum(stow.Claimed, itemId) - Sum(stow.Items, itemId));
            var stowPlanned = (uint)(stowLines.Sum(line => (long)line.Requested) + notCarried);
            var stowed = (uint)stowLines.Sum(line => (long)line.Stowed);
            var codes = session.NotCollected.Where(outcome => outcome.ItemId == itemId).Select(outcome => outcome.Code)
                .Concat(all.SelectMany(collection => collection.Outcomes).Where(outcome => outcome.ItemId == itemId).Select(outcome => outcome.Code))
                .Concat(all.Where(collection => collection.Lines.Any(line => line.ItemId == itemId))
                    .SelectMany(collection => new[]
                    {
                        collection.State == CrossRegionCollectionState.NotCollected ? collection.OutcomeCode : null,
                        !collection.RetrievalAttempted && collection.Lines.Any(line => line.ItemId == itemId && line.RetainerQuantity > 0)
                            ? collection.RetrievalOutcomeCode : null,
                    }))
                // Every unstowed unit carries a reason: the line's own, else the stow's outcome.
                .Concat(stowLines.Where(line => line.Stowed < line.Requested)
                    .Select(line => line.Code is "NoRoom" or "PartlyStowed" or "NotStowed" or LineCodeNotCarried
                        ? line.Code
                        : session.Stow?.OutcomeCode ?? "NotStowed"))
                .Concat(notCarried > 0 ? [LineCodeNotCarried] : [])
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (collected < counted || stowed < stowPlanned || codes.Count > 0)
                results.Add(new(itemId, name, bought, counted, collected, stowPlanned, stowed, codes));
        }
        return results;
    }
}
