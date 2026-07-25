using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Travel;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

public enum ShardAcquisitionCheckpointPhase
{
    Ready,
    ClosingMarketBoard,
    ReturningHome,
    WaitingForHomeWorld,
    TravelingToEstate,
    OpeningBell,
    SubmittingDeposit,
    WaitingForDeposit,
    Failed,
}

public sealed record ShardAcquisitionCheckpointSnapshot(
    bool IsEnabled,
    bool IsActive,
    bool IsFinal,
    ShardAcquisitionCheckpointPhase Phase,
    string Message,
    string? OperationId,
    IReadOnlyDictionary<uint, int> OutstandingByItem);

public sealed record ShardAcquisitionCheckpointResult(bool Success, bool Enabled, string Message);
public sealed record ShardAcquisitionCheckpointTickResult(bool Worked, bool Completed, bool Failed, bool ResumeRoute, string Message);

public interface IShardAcquisitionCheckpointRuntime
{
    DateTimeOffset UtcNow { get; }
    bool TryGetOwner(out QuartermasterOwner owner);
    string? CurrentWorldName { get; }
    IReadOnlyDictionary<uint, int> CountPlayerShards();
    bool TryCloseMarketBoardWindows();
    bool ProcessCommand(string command);
    bool TryIsLifestreamBusy(out bool busy);
    PrivateEstateTravelResult TryTravelToPrivateEstate();
    bool TryOpenSummoningBell();
}

public interface IShardAcquisitionCheckpointStateStore
{
    ShardAcquisitionCheckpointState? Restore();
    void Save(ShardAcquisitionCheckpointState? state);
}

public sealed record ShardAcquisitionCheckpointLot
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public int Quantity { get; init; }
}

public sealed record ShardAcquisitionCheckpointState
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string RouteRequestId { get; init; } = string.Empty;
    public string RouteRunId { get; init; } = string.Empty;
    public QuartermasterOwner Owner { get; init; } = new(0, 0, string.Empty, null);
    public Dictionary<uint, int> BaselineByItem { get; init; } = [];
    public Dictionary<uint, string> ItemNamesByItem { get; init; } = [];
    public Dictionary<uint, int> ConfirmedPurchasesByItem { get; init; } = [];
    public Dictionary<uint, int> ConfirmedDepositsByItem { get; init; } = [];
    public List<ShardAcquisitionCheckpointLot> RemainingLots { get; init; } = [];
    public ShardAcquisitionCheckpointPhase Phase { get; init; } = ShardAcquisitionCheckpointPhase.Ready;
    public bool IsFinal { get; init; }
    public string? OperationId { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class ConfigurationShardAcquisitionCheckpointStateStore : IShardAcquisitionCheckpointStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Configuration configuration;
    private readonly Action save;

    public ConfigurationShardAcquisitionCheckpointStateStore(Configuration configuration, Action? save = null)
    {
        this.configuration = configuration;
        this.save = save ?? configuration.Save;
    }

    public ShardAcquisitionCheckpointState? Restore()
    {
        if (string.IsNullOrWhiteSpace(configuration.ShardAcquisitionCheckpointStateJson))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<ShardAcquisitionCheckpointState>(
                configuration.ShardAcquisitionCheckpointStateJson,
                JsonOptions);
            return state?.SchemaVersion == ShardAcquisitionCheckpointState.CurrentSchemaVersion ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(ShardAcquisitionCheckpointState? state)
    {
        configuration.ShardAcquisitionCheckpointStateJson = state is null
            ? null
            : JsonSerializer.Serialize(state, JsonOptions);
        save();
    }
}

public interface IShardAcquisitionCheckpointCoordinator
{
    bool IsActive { get; }
    ShardAcquisitionCheckpointSnapshot Snapshot { get; }
    ShardAcquisitionCheckpointResult Prepare(MarketAcquisitionPlan plan, string routeRunId);
    bool RecordConfirmedPurchase(MarketBoardPurchaseCandidate candidate);
    bool RequestFinalCheckpoint();
    ShardAcquisitionCheckpointTickResult Tick();
    void Reset();
}

public sealed class ShardAcquisitionCheckpointCoordinator : IShardAcquisitionCheckpointCoordinator
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BellTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DepositTimeout = TimeSpan.FromMinutes(2);
    private readonly QuartermasterIpcClient quartermaster;
    private readonly IShardAcquisitionCheckpointRuntime runtime;
    private readonly IShardAcquisitionCheckpointStateStore store;
    private ShardAcquisitionCheckpointState? state;

    public ShardAcquisitionCheckpointCoordinator(
        QuartermasterIpcClient quartermaster,
        IShardAcquisitionCheckpointRuntime runtime,
        IShardAcquisitionCheckpointStateStore store)
    {
        this.quartermaster = quartermaster;
        this.runtime = runtime;
        this.store = store;
        state = store.Restore();
    }

    public bool IsActive => state?.Phase is not null and not ShardAcquisitionCheckpointPhase.Ready;

    public ShardAcquisitionCheckpointSnapshot Snapshot
    {
        get
        {
            var current = state;
            return current is null
                ? new(false, false, false, ShardAcquisitionCheckpointPhase.Ready, "Shard storage checkpoints are not needed for this route.", null, new Dictionary<uint, int>())
                : new(true, IsActive, current.IsFinal, current.Phase, current.Message, current.OperationId, Outstanding(current));
        }
    }

    public ShardAcquisitionCheckpointResult Prepare(MarketAcquisitionPlan plan, string routeRunId)
    {
        var lots = plan.WorldBatches
            .SelectMany(batch => batch.ItemSubtasks)
            .Where(subtask => ElementalCurrencyCatalog.IsShard(subtask.ItemId))
            .SelectMany(subtask => subtask.Listings.Select(listing => new ShardAcquisitionCheckpointLot
            {
                ItemId = subtask.ItemId,
                ItemName = subtask.ItemName ?? listing.ItemName ?? $"Shard {subtask.ItemId}",
                ListingId = listing.ListingId,
                Quantity = checked((int)listing.Quantity),
            }))
            .Where(lot => lot.Quantity > 0)
            .ToList();
        if (lots.Count == 0)
        {
            if (state is not null &&
                (IsActive || Outstanding(state).Values.Any(quantity => quantity > 0)))
            {
                return new(false, true, "A persisted shard storage checkpoint must be reconciled before starting a non-shard route.");
            }

            state = null;
            store.Save(null);
            return new(true, false, "This route contains no shard purchases.");
        }
        if (!runtime.TryGetOwner(out var owner))
            return new(false, true, "Shard storage preflight requires current character identity.");
        if (state is not null &&
            (state.Owner.LocalContentId != owner.LocalContentId ||
             state.Owner.HomeWorldId != owner.HomeWorldId ||
             !state.RouteRequestId.Equals(plan.RequestId, StringComparison.Ordinal)) &&
            (IsActive || Outstanding(state).Values.Any(quantity => quantity > 0)))
        {
            return new(false, true, "A persisted shard storage checkpoint for another route or character must be reconciled first.");
        }
        if (state is not null &&
            state.Owner.LocalContentId == owner.LocalContentId &&
            state.Owner.HomeWorldId == owner.HomeWorldId &&
            state.RouteRequestId.Equals(plan.RequestId, StringComparison.Ordinal) &&
            (IsActive || Outstanding(state).Values.Any(quantity => quantity > 0)))
        {
            state = state with { RouteRunId = routeRunId };
            if (state.Phase == ShardAcquisitionCheckpointPhase.Failed)
                return new(false, true, $"Persisted shard storage recovery is required: {state.Message}");
            if (state.Phase == ShardAcquisitionCheckpointPhase.Ready)
                BeginCheckpoint(isFinal: false, "Recovering purchased shards before acquisition resumes.");
            else
                Persist();
            return new(true, true, "Recovered the persisted shard purchase ledger and storage checkpoint.");
        }
        if (!quartermaster.TryGetCapabilities(out var capabilities, out var error) ||
            !capabilities!.Capabilities.Contains(QuartermasterIpcClient.AutomaticElementalDepositCapability, StringComparer.Ordinal))
            return new(false, true, $"Shard storage preflight failed: {error}");
        if (!quartermaster.TryGetSnapshot(out var snapshot, out error))
            return new(false, true, $"Shard storage preflight failed: {error}");
        if (snapshot!.Owner.LocalContentId != owner.LocalContentId || snapshot.Owner.HomeWorldId != owner.HomeWorldId)
            return new(false, true, "Quartermaster snapshot owner does not match the purchasing character.");

        var baseline = runtime.CountPlayerShards()
            .Where(entry => ElementalCurrencyCatalog.IsShard(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var planned = lots.GroupBy(lot => lot.ItemId).ToDictionary(group => group.Key, group => group.Sum(lot => lot.Quantity));
        var capacity = CalculateKnownCapacity(snapshot, planned.Keys);
        foreach (var entry in planned)
        {
            var protectedQuantity = baseline.GetValueOrDefault(entry.Key);
            if (lots.Where(lot => lot.ItemId == entry.Key).Any(lot => protectedQuantity + lot.Quantity > ElementalCurrencyCatalog.PerItemCapacity))
                return new(false, true, $"{lots.First(lot => lot.ItemId == entry.Key).ItemName} has a market lot too large to preserve the current {protectedQuantity:N0} baseline.");
            if (capacity.GetValueOrDefault(entry.Key) < entry.Value)
                return new(false, true, $"Known Quartermaster capacity for {lots.First(lot => lot.ItemId == entry.Key).ItemName} is {capacity.GetValueOrDefault(entry.Key):N0}, below the planned {entry.Value:N0}.");
        }

        state = new ShardAcquisitionCheckpointState
        {
            RouteRequestId = plan.RequestId,
            RouteRunId = routeRunId,
            Owner = owner,
            BaselineByItem = baseline,
            ItemNamesByItem = lots
                .GroupBy(lot => lot.ItemId)
                .ToDictionary(group => group.Key, group => group.First().ItemName),
            RemainingLots = lots,
            Message = "Shard storage preflight passed.",
        };
        Persist();
        return new(true, true, state.Message);
    }

    public bool RecordConfirmedPurchase(MarketBoardPurchaseCandidate candidate)
    {
        if (state is null || !ElementalCurrencyCatalog.IsShard(candidate.ItemId))
            return false;
        if (state.Phase != ShardAcquisitionCheckpointPhase.Ready)
            throw new InvalidOperationException("A shard purchase was confirmed while a storage checkpoint was already active.");

        var purchases = new Dictionary<uint, int>(state.ConfirmedPurchasesByItem)
        {
            [candidate.ItemId] = checked(state.ConfirmedPurchasesByItem.GetValueOrDefault(candidate.ItemId) + checked((int)candidate.Quantity)),
        };
        var remaining = state.RemainingLots.ToList();
        var matching = remaining.FindIndex(lot =>
            lot.ItemId == candidate.ItemId &&
            (lot.ListingId.Equals(candidate.ListingId, StringComparison.Ordinal) || lot.Quantity == candidate.Quantity));
        if (matching >= 0)
            remaining.RemoveAt(matching);
        state = state with
        {
            ConfirmedPurchasesByItem = purchases,
            RemainingLots = remaining,
            Message = $"Recorded confirmed shard purchase of {candidate.Quantity:N0}.",
        };

        var next = remaining.FirstOrDefault();
        var physical = runtime.CountPlayerShards();
        var needsCheckpoint = false;
        if (next is not null &&
            checked(physical.GetValueOrDefault(next.ItemId) + next.Quantity) > ElementalCurrencyCatalog.PerItemCapacity)
        {
            needsCheckpoint = true;
            BeginCheckpoint(isFinal: false, $"Returning home before the next {next.ItemName} lot would exceed character capacity.");
        }
        else
        {
            Persist();
        }
        return needsCheckpoint;
    }

    public bool RequestFinalCheckpoint()
    {
        if (state is null || IsActive || Outstanding(state).Values.All(quantity => quantity <= 0))
            return false;
        BeginCheckpoint(isFinal: true, "Acquisition complete; returning home to store purchased shards.");
        return true;
    }

    public ShardAcquisitionCheckpointTickResult Tick()
    {
        if (state is null || state.Phase == ShardAcquisitionCheckpointPhase.Ready)
            return new(false, false, false, false, Snapshot.Message);
        if (state.Phase == ShardAcquisitionCheckpointPhase.Failed)
            return new(false, false, true, false, state.Message);
        if (runtime.UtcNow > state.DeadlineUtc)
            return Fail($"Shard storage checkpoint timed out during {state.Phase}.");
        if (!runtime.TryGetOwner(out var owner) || owner.LocalContentId != state.Owner.LocalContentId || owner.HomeWorldId != state.Owner.HomeWorldId)
            return Fail("Shard storage checkpoint stopped because the current character no longer matches its owner.");

        switch (state.Phase)
        {
            case ShardAcquisitionCheckpointPhase.ClosingMarketBoard:
                runtime.TryCloseMarketBoardWindows();
                return MoveTo(
                    CurrentWorldIsHome()
                        ? ShardAcquisitionCheckpointPhase.TravelingToEstate
                        : ShardAcquisitionCheckpointPhase.ReturningHome,
                    TravelTimeout,
                    CurrentWorldIsHome() ? "Traveling to the private estate." : "Returning to the home world.");

            case ShardAcquisitionCheckpointPhase.ReturningHome:
                if (CurrentWorldIsHome())
                    return MoveTo(ShardAcquisitionCheckpointPhase.TravelingToEstate, TravelTimeout, "Home world reached; traveling to the private estate.");
                if (!runtime.ProcessCommand($"/li {state.Owner.HomeWorldName}"))
                    return Fail("Lifestream rejected home-world travel.");
                return MoveTo(ShardAcquisitionCheckpointPhase.WaitingForHomeWorld, TravelTimeout, $"Waiting to arrive on {state.Owner.HomeWorldName}.");

            case ShardAcquisitionCheckpointPhase.WaitingForHomeWorld:
                if (CurrentWorldIsHome())
                    return MoveTo(ShardAcquisitionCheckpointPhase.TravelingToEstate, TravelTimeout, "Home world reached; traveling to the private estate.");
                return Waiting($"Waiting to arrive on {state.Owner.HomeWorldName}.");

            case ShardAcquisitionCheckpointPhase.TravelingToEstate:
                if (!runtime.TryIsLifestreamBusy(out var traveling))
                    return Fail("Lifestream busy state is unavailable.");
                if (traveling)
                    return Waiting("Waiting for Lifestream travel to finish.");
                var travel = runtime.TryTravelToPrivateEstate();
                if (travel.State == PrivateEstateTravelState.Busy)
                    return Waiting(travel.Message);
                if (!travel.Submitted)
                    return Fail($"Private-estate travel failed: {travel.Message}");
                return MoveTo(ShardAcquisitionCheckpointPhase.OpeningBell, BellTimeout, "Private-estate travel accepted; waiting to approach the summoning bell.");

            case ShardAcquisitionCheckpointPhase.OpeningBell:
                if (!runtime.TryIsLifestreamBusy(out var busy))
                    return Fail("Lifestream busy state is unavailable.");
                if (busy)
                    return Waiting("Waiting for private-estate travel to finish.");
                if (!runtime.TryOpenSummoningBell())
                    return Waiting("Waiting for the configured private-estate summoning bell.");
                return MoveTo(ShardAcquisitionCheckpointPhase.SubmittingDeposit, BellTimeout, "Summoning bell approach accepted.");

            case ShardAcquisitionCheckpointPhase.SubmittingDeposit:
                if (!runtime.TryIsLifestreamBusy(out var approachingBell))
                    return Fail("Lifestream busy state is unavailable.");
                if (approachingBell)
                    return Waiting("Waiting for the summoning-bell approach to finish.");
                try
                {
                    return SubmitDeposit();
                }
                catch (Exception exception)
                {
                    return Fail($"Shard deposit authorization could not be constructed safely: {exception.Message}");
                }

            case ShardAcquisitionCheckpointPhase.WaitingForDeposit:
                return ObserveDeposit();

            default:
                return Fail($"Unsupported shard storage phase {state.Phase}.");
        }
    }

    public void Reset()
    {
        state = null;
        store.Save(null);
    }

    private ShardAcquisitionCheckpointTickResult SubmitDeposit()
    {
        var outstanding = Outstanding(state!);
        var physical = runtime.CountPlayerShards();
        var targets = outstanding
            .Where(entry => entry.Value > 0)
            .Select(entry =>
            {
                var transferable = Math.Max(0, physical.GetValueOrDefault(entry.Key) - state!.BaselineByItem.GetValueOrDefault(entry.Key));
                if (transferable != entry.Value)
                    throw new InvalidOperationException($"Physical shard delta for item {entry.Key} is {transferable:N0}, but the durable ledger authorizes {entry.Value:N0}.");
                var name = state.ItemNamesByItem.GetValueOrDefault(entry.Key) ?? $"Shard {entry.Key}";
                return new QuartermasterElementalDepositTarget(entry.Key, name, entry.Value);
            })
            .ToImmutableArray();
        if (targets.IsDefaultOrEmpty)
            return CompleteCheckpoint();

        var operationId = state!.OperationId ?? $"mmf-shards-{Guid.NewGuid():N}";
        var request = new QuartermasterElementalDepositRequest(
            operationId,
            operationId,
            runtime.UtcNow,
            state.Owner,
            targets);
        if (!quartermaster.TrySubmitElementalDeposit(request, out var acknowledgement, out var error))
            return Fail(error);
        if (acknowledgement is null || !acknowledgement.Accepted)
            return Fail(acknowledgement?.Message ?? "Quartermaster rejected the elemental deposit.");
        state = state with
        {
            OperationId = operationId,
            Phase = ShardAcquisitionCheckpointPhase.WaitingForDeposit,
            DeadlineUtc = runtime.UtcNow.Add(DepositTimeout),
            Message = "Quartermaster accepted the shard deposit; waiting for exact transfer receipts.",
        };
        Persist();
        return new(true, false, false, false, state.Message);
    }

    private ShardAcquisitionCheckpointTickResult ObserveDeposit()
    {
        if (!quartermaster.TryGetOperation(
                state!.OperationId!,
                new QuartermasterOwnerScope(state.Owner.LocalContentId, state.Owner.HomeWorldId, state.Owner.CharacterName, state.Owner.HomeWorldName),
                out var operation,
                out var error))
            return Waiting(error);
        if (operation is null || !operation.IsTerminal)
            return Waiting(operation?.Message ?? "Waiting for Quartermaster.");
        if (!operation.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            return Fail($"Quartermaster deposit ended as {operation.Status}: {operation.Message}");

        var deposited = new Dictionary<uint, int>(state.ConfirmedDepositsByItem);
        foreach (var receipt in operation.Receipts.Where(receipt => receipt.ItemId is not null && receipt.Quantity is > 0))
            deposited[receipt.ItemId!.Value] = checked(deposited.GetValueOrDefault(receipt.ItemId.Value) + receipt.Quantity!.Value);
        var expected = Outstanding(state);
        if (expected.Any(entry => deposited.GetValueOrDefault(entry.Key) - state.ConfirmedDepositsByItem.GetValueOrDefault(entry.Key) != entry.Value))
            return Fail("Quartermaster succeeded without exact receipts matching the authorized shard delta.");
        state = state with { ConfirmedDepositsByItem = deposited };
        return CompleteCheckpoint();
    }

    private ShardAcquisitionCheckpointTickResult CompleteCheckpoint()
    {
        var resume = !state!.IsFinal;
        state = state with
        {
            Phase = ShardAcquisitionCheckpointPhase.Ready,
            IsFinal = false,
            OperationId = null,
            DeadlineUtc = default,
            Message = resume ? "Shard storage checkpoint complete; resuming acquisition." : "Final shard storage checkpoint complete.",
        };
        Persist();
        return new(true, true, false, resume, state.Message);
    }

    private void BeginCheckpoint(bool isFinal, string message)
    {
        state = state! with
        {
            Phase = ShardAcquisitionCheckpointPhase.ClosingMarketBoard,
            IsFinal = isFinal,
            OperationId = null,
            DeadlineUtc = runtime.UtcNow.Add(TravelTimeout),
            Message = message,
        };
        Persist();
    }

    private ShardAcquisitionCheckpointTickResult MoveTo(ShardAcquisitionCheckpointPhase phase, TimeSpan timeout, string message)
    {
        state = state! with { Phase = phase, DeadlineUtc = runtime.UtcNow.Add(timeout), Message = message };
        Persist();
        return new(true, false, false, false, message);
    }

    private ShardAcquisitionCheckpointTickResult Waiting(string message)
    {
        if (!state!.Message.Equals(message, StringComparison.Ordinal))
        {
            state = state with { Message = message };
            Persist();
        }
        return new(false, false, false, false, message);
    }

    private ShardAcquisitionCheckpointTickResult Fail(string message)
    {
        state = state! with { Phase = ShardAcquisitionCheckpointPhase.Failed, Message = message };
        Persist();
        return new(true, false, true, false, message);
    }

    private bool CurrentWorldIsHome() =>
        !string.IsNullOrWhiteSpace(runtime.CurrentWorldName) &&
        runtime.CurrentWorldName.Equals(state!.Owner.HomeWorldName, StringComparison.OrdinalIgnoreCase);

    private void Persist() => store.Save(state);

    private static Dictionary<uint, int> Outstanding(ShardAcquisitionCheckpointState state) =>
        state.ConfirmedPurchasesByItem
            .ToDictionary(entry => entry.Key, entry => Math.Max(0, entry.Value - state.ConfirmedDepositsByItem.GetValueOrDefault(entry.Key)));

    private static Dictionary<uint, int> CalculateKnownCapacity(QuartermasterSnapshot snapshot, IEnumerable<uint> itemIds)
    {
        var wanted = itemIds.ToArray();
        var capacity = wanted.ToDictionary(itemId => itemId, _ => 0);
        foreach (var retainer in snapshot.Retainers)
        {
            var crystals = retainer.Bags.FirstOrDefault(bag => bag.BagName.Equals("RetainerCrystals", StringComparison.Ordinal));
            if (crystals is null)
                continue;
            foreach (var itemId in wanted)
            {
                var stored = crystals.Items.Where(item => item.ItemId == itemId).Sum(item => checked((int)item.Quantity));
                capacity[itemId] = checked(capacity[itemId] + Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - stored));
            }
        }
        return capacity;
    }
}
