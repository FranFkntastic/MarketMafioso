using Dalamud.Game.ClientState.Conditions;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.SpecTests.MarketDiagnostics;

// A stuck or leaky market-board lockout cannot be reproduced without provoking a real server lockout, and its
// failure modes are silent: listings stop reaching Universalis (lost sales) or MMF keeps searching a locked
// market (automation evidence). These tests pin the lockout -> relog -> resume contract end to end.
public sealed class MarketLockoutRelogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstRateLimitLocksEveryOwnerOutUntilLogoutThenLogin()
    {
        var persisted = new PersistedMarketBoardSessionCircuitBreakerState();
        var gate = new MarketBoardBrowseOperationGate(() => Now, persisted, getCurrentWorld: () => "Siren");
        var runtime = new GateRuntime(gate);

        Assert.True(runtime.TryRequestExactItem(MarketBoardBrowseOwner.RetainerListingRefresh, 21792, out _));
        gate.ObserveHeader(MarketBoardBrowseOperationGate.RateLimitStatus, 0);

        Assert.Equal(MarketBoardBrowseOperationGate.LockoutFailureCode, gate.Snapshot.FailureCode);
        Assert.True(persisted.RelogRequired);
        Assert.Equal("Siren", gate.Lockout.TriggerWorld);
        Assert.Equal(21792u, gate.Lockout.TriggerItemId);
        foreach (var owner in Enum.GetValues<MarketBoardBrowseOwner>())
            Assert.False(gate.TryBegin(owner, 1, out _));

        // A plugin reload while logged in, then a login without a logout (e.g. a world visit), must not clear it.
        gate = new MarketBoardBrowseOperationGate(() => Now, persisted);
        gate.ObserveInitialSession(isLoggedIn: true);
        Assert.False(gate.ObserveLogin());
        Assert.True(gate.Lockout.Active);

        // A plugin loaded at the title screen has already seen the logout; the next login clears it.
        gate = new MarketBoardBrowseOperationGate(() => Now, persisted);
        gate.ObserveInitialSession(isLoggedIn: false);
        Assert.True(gate.Lockout.Active);
        Assert.True(gate.ObserveLogin());
        Assert.False(gate.Lockout.Active);
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 1, out _));
    }

    [Fact]
    public void UserSearchThatTripsTheLockoutLocksMmfOutToo()
    {
        var persisted = new PersistedMarketBoardSessionCircuitBreakerState();
        var gate = new MarketBoardBrowseOperationGate(() => Now, persisted, getCurrentWorld: () => "Siren");

        // No MMF operation is active: this header answers the user's own market-board search.
        gate.ObserveHeader(MarketBoardBrowseOperationGate.RateLimitStatus, 0);

        Assert.True(gate.Lockout.Active);
        Assert.Equal(MarketBoardBrowseOperationGate.UnownedTriggerOwner, gate.Lockout.TriggerOwner);
        Assert.Null(gate.Snapshot.FailureCode);
        Assert.False(gate.TryBegin(MarketBoardBrowseOwner.RetainerListingRefresh, 1, out var refused));
        Assert.Equal(MarketBoardBrowseOperationGate.LockoutFailureCode, refused.FailureCode);
    }

    [Fact]
    public void RelogIsRefusedWhileTheCharacterOrMmfIsBusyOrAwayFromHome()
    {
        var idle = new MarketRelogGameState
        {
            LoggedIn = true,
            Name = "Test Character",
            HomeWorld = "Siren",
            CurrentDataCenterId = 4,
            HomeDataCenterId = 4,
        };
        string? Refusal(MarketRelogGameState state, bool browseActive = false) =>
            MarketRelogPolicy.GetRefusal(lockoutActive: true, relogInFlight: false, browseActive, state);

        Assert.Null(Refusal(idle));
        Assert.NotNull(MarketRelogPolicy.GetRefusal(lockoutActive: false, relogInFlight: false, browseActive: false, idle));
        Assert.NotNull(MarketRelogPolicy.GetRefusal(lockoutActive: true, relogInFlight: true, browseActive: false, idle));
        Assert.NotNull(Refusal(idle, browseActive: true));
        Assert.NotNull(Refusal(idle with { LoggedIn = false }));
        Assert.Equal("A market listing purchase is in progress.", Refusal(idle with { AutomationBusyReason = "A market listing purchase is in progress." }));
        Assert.NotNull(Refusal(idle with { RetainerSessionOpen = true }));
        Assert.Equal(MarketRelogPolicy.WrongDataCenterReason, Refusal(idle with { CurrentDataCenterId = 5 }));
        Assert.NotNull(Refusal(idle with { CurrentDataCenterId = null }));

        foreach (var flag in new[]
                 {
                     ConditionFlag.InCombat, ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95,
                     ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.OccupiedInCutSceneEvent,
                     ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.BetweenAreas,
                     ConditionFlag.BetweenAreas51, ConditionFlag.OccupiedSummoningBell, ConditionFlag.OccupiedInQuestEvent,
                     ConditionFlag.OccupiedInEvent, ConditionFlag.LoggingOut,
                 })
        {
            Assert.NotNull(Refusal(idle with { IsConditionSet = candidate => candidate == flag }));
        }
    }

    [Fact]
    public void PersistedStrikeFromEarlierBuildIsRepairedToLockout()
    {
        var persisted = new PersistedMarketBoardSessionCircuitBreakerState { RateLimitCount = 2 };

        var gate = new MarketBoardBrowseOperationGate(() => Now, persisted);

        Assert.True(persisted.RelogRequired);
        Assert.False(gate.TryBegin(MarketBoardBrowseOwner.RetainerListingRefresh, 1, out var refused));
        Assert.Equal(MarketBoardBrowseOperationGate.LockoutFailureCode, refused.FailureCode);
    }

    [Fact]
    public void RefreshQueuePausesWithoutBlockingAndResumesAfterRelog()
    {
        var gate = new MarketBoardBrowseOperationGate(() => Now);
        var runtime = new GateRuntime(gate);
        var source = new ListingSource(new RetainerListingRefreshCandidate(21792, "A"), new RetainerListingRefreshCandidate(21793, "B"));
        var config = CreateConfig();
        var coordinator = new RetainerListingRefreshCoordinator(config, source, runtime, () => { }, _ => { }, nextSuccessDelay: () => TimeSpan.Zero);

        coordinator.Tick(Now, dispatchReady: true);
        Assert.Equal(1, runtime.Dispatches);
        gate.ObserveHeader(MarketBoardBrowseOperationGate.RateLimitStatus, 0);

        for (var second = 1; second <= 120; second++)
            coordinator.Tick(Now.AddSeconds(second), dispatchReady: true);

        Assert.Equal(1, runtime.Dispatches);
        Assert.True(config.RetainerListingRefresh.WaitingForRelog);
        Assert.Equal(2, coordinator.PendingCount);
        Assert.Equal(0, coordinator.BlockedCount);

        gate.ObserveLogout(0, 10000);
        gate.ObserveLogin();
        coordinator.Tick(Now.AddSeconds(121), dispatchReady: true);

        Assert.False(config.RetainerListingRefresh.WaitingForRelog);
        Assert.Equal(2, runtime.Dispatches);
    }

    [Fact]
    public void LockoutBlocksFromEarlierBuildsRejoinTheQueueAndNewListingsRequeueBlockedItems()
    {
        var gate = new MarketBoardBrowseOperationGate(() => Now);
        var runtime = new GateRuntime(gate);
        var config = CreateConfig();
        config.RetainerListingRefresh.Items.Add(Blocked(21792, "MarketBoardSessionExpired"));
        config.RetainerListingRefresh.Items.Add(Blocked(21793, "PageItemMismatch"));
        var source = new ListingSource();

        var coordinator = new RetainerListingRefreshCoordinator(config, source, runtime, () => { }, _ => { });

        Assert.Equal(RetainerListingRefreshItemState.Deferred, config.RetainerListingRefresh.Items.Single(i => i.ItemId == 21792).State);
        Assert.Equal(1, coordinator.BlockedCount);

        source.Publish(new RetainerListingRefreshCandidate(21793, "B"));
        coordinator.NotifyListingCaptureChanged();
        coordinator.Tick(Now, dispatchReady: false);

        Assert.Equal(0, coordinator.BlockedCount);
    }

    private static Configuration CreateConfig()
    {
        var config = new Configuration
        {
            EnableMarketAcquisition = true,
            EnableRetainerListingRefresh = true,
        };
        config.RetainerListingRefresh = new PersistedRetainerListingRefreshState { LastObservedCaptureId = "baseline" };
        return config;
    }

    private static PersistedRetainerListingRefreshItem Blocked(uint itemId, string code) => new()
    {
        ItemId = itemId,
        State = RetainerListingRefreshItemState.Blocked,
        LastCode = code,
        Attempts = 3,
    };

    private sealed class ListingSource(params RetainerListingRefreshCandidate[] initial) : IRetainerListingRefreshSource
    {
        private RetainerListingRefreshSnapshot snapshot = new(initial, initial.Length == 0 ? "baseline" : "capture-1");
        private int revision = 1;

        public void Publish(params RetainerListingRefreshCandidate[] items) =>
            snapshot = new RetainerListingRefreshSnapshot(items, $"capture-{++revision}");

        public bool TryRead(out RetainerListingRefreshSnapshot? result, out string error)
        {
            result = snapshot;
            error = string.Empty;
            return true;
        }
    }

    private sealed class GateRuntime(MarketBoardBrowseOperationGate gate) : IHeadlessMarketBoardBrowseRuntime
    {
        public int Dispatches { get; private set; }
        public bool IsAvailable => true;
        public string AvailabilityMessage => string.Empty;
        public MarketBoardBrowseSnapshot Snapshot => gate.Snapshot;
        public MarketBoardLockoutStatus Lockout => gate.Lockout;

        public bool TryBegin(MarketBoardBrowseOwner owner, uint itemId, out MarketBoardBrowseSnapshot snapshot) =>
            gate.TryBegin(owner, itemId, out snapshot);

        public bool TryClaimActivation(MarketBoardBrowseOwner owner, uint itemId, out MarketBoardBrowseSnapshot snapshot) =>
            gate.TryClaimActivation(owner, itemId, out snapshot);

        public bool TryAbandon(MarketBoardBrowseOwner owner, string operationId, string reason, out MarketBoardBrowseSnapshot snapshot) =>
            gate.TryAbandon(owner, operationId, reason, out snapshot);

        public bool TryRequestExactItem(MarketBoardBrowseOwner owner, uint itemId, out MarketBoardBrowseSnapshot snapshot)
        {
            if (!gate.TryBegin(owner, itemId, out snapshot) || !gate.TryClaimActivation(owner, itemId, out snapshot))
                return false;

            Dispatches++;
            gate.ObserveRequest(itemId, accepted: true);
            snapshot = gate.Snapshot;
            return true;
        }
    }
}
