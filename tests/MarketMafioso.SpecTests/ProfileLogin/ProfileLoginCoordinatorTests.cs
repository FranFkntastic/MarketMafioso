using System.Text.Json;
using Franthropy.Dalamud.Travel;
using MarketMafioso.ProfileLogin;

namespace MarketMafioso.SpecTests.ProfileLogin;

public sealed class ProfileLoginCoordinatorTests
{
    [Fact]
    public void NotReadyThenSubmitted_DispatchesExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.Driver.Results.Enqueue(new(false, "NotReady", "loading"));
        fixture.Driver.Results.Enqueue(new(true, "Submitted", "accepted", "TitleScreen"));
        fixture.WriteRequest();

        fixture.Tick();
        Assert.Equal("WaitingForDriver", fixture.ReadReceipt().Phase);
        fixture.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("Submitted", fixture.ReadReceipt().Phase);
        fixture.Tick(TimeSpan.FromSeconds(2));

        Assert.Equal(2, fixture.Driver.Calls);
    }

    [Fact]
    public void ExistingSubmittingReceipt_IsNeverReplayed()
    {
        using var fixture = new Fixture();
        fixture.WriteRequest();
        fixture.WriteReceipt("Submitting");

        fixture.Tick();

        Assert.Equal(0, fixture.Driver.Calls);
        Assert.Equal("AmbiguousSubmission", fixture.ReadReceipt().Code);
    }

    [Fact]
    public void IpcFailure_IsAmbiguousAndNeverRetried()
    {
        using var fixture = new Fixture();
        fixture.Driver.Results.Enqueue(new(false, "IpcFailure", "provider disconnected"));
        fixture.WriteRequest();
        fixture.Tick();
        fixture.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("Blocked", fixture.ReadReceipt().Phase);
        Assert.Equal(1, fixture.Driver.Calls);
    }

    [Fact]
    public void SubmittedOperation_DoesNotContinueInReplacementProcess()
    {
        using var fixture = new Fixture();
        fixture.WriteRequest();
        fixture.WriteReceipt("Submitted");
        fixture.CurrentProcessId = 84;
        fixture.Tick();
        Assert.Equal("ProcessReplaced", fixture.ReadReceipt().Code);
        Assert.Equal(0, fixture.Driver.Calls);
    }

    [Fact]
    public void CorruptReceipt_FailsClosedWithoutDispatch()
    {
        using var fixture = new Fixture();
        fixture.WriteRequest();
        fixture.WriteRawReceipt("not json");
        fixture.Tick();
        Assert.Equal("ReceiptCorrupt", fixture.ReadReceipt().Code);
        Assert.Equal(0, fixture.Driver.Calls);
    }

    [Fact]
    public void ForeignProfileAndStaleProcess_AreRejectedWithoutDispatch()
    {
        using var foreign = new Fixture();
        foreign.WriteRequest(profileId: "other-profile");
        foreign.Tick();
        Assert.Equal(0, foreign.Driver.Calls);

        using var stale = new Fixture(processStartedAt: Fixture.Now.AddMinutes(-2));
        stale.WriteRequest(minimumProcessStart: Fixture.Now.AddMinutes(-1));
        stale.Tick();
        Assert.Equal(0, stale.Driver.Calls);
        Assert.Equal("StaleProcess", stale.ReadReceipt().Code);
    }

    [Fact]
    public void SubmittedRequest_CompletesOnlyForExactIdentity()
    {
        using var exact = new Fixture(identity: (true, "Eriana Ning", "Siren"));
        exact.WriteRequest();
        exact.WriteReceipt("Submitted");
        exact.Tick();
        Assert.Equal("LoggedIn", exact.ReadReceipt().Phase);

        using var mismatch = new Fixture(identity: (true, "Someone Else", "Siren"));
        mismatch.WriteRequest();
        mismatch.WriteReceipt("Submitted");
        mismatch.Tick();
        Assert.Equal("IdentityMismatch", mismatch.ReadReceipt().Code);
        Assert.Equal(0, mismatch.Driver.Calls);
    }

    [Fact]
    public void ExpiredRequest_DoesNotDispatch()
    {
        using var fixture = new Fixture();
        fixture.WriteRequest(submissionExpiry: Fixture.Now.AddSeconds(-1));
        fixture.Tick();
        Assert.Equal("Expired", fixture.ReadReceipt().Phase);
        Assert.Equal(0, fixture.Driver.Calls);
    }

    [Fact]
    public void SubmittedRequest_TimesOutWithoutRedispatch()
    {
        using var fixture = new Fixture();
        fixture.WriteRequest(completionExpiry: Fixture.Now.AddSeconds(-1));
        fixture.WriteReceipt("Submitted");
        fixture.Tick();
        Assert.Equal("CompletionTimeout", fixture.ReadReceipt().Code);
        Assert.Equal(0, fixture.Driver.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        private readonly string root = Path.Combine(Path.GetTempPath(), "mmf-profile-login-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset processStartedAt;
        private readonly (bool LoggedIn, string Name, string HomeWorld) identity;
        private DateTimeOffset clock = Now;
        public FakeDriver Driver { get; } = new();
        public int CurrentProcessId { get; set; } = 42;
        private string ProtocolRoot => Path.Combine(root, "profile-manager-login");
        private string OperationRoot => Path.Combine(ProtocolRoot, "operations", "operation-1");

        public Fixture(DateTimeOffset? processStartedAt = null, (bool, string, string)? identity = null)
        {
            this.processStartedAt = processStartedAt ?? Now.AddSeconds(-5);
            this.identity = identity ?? (false, "", "");
            ProfileLoginCoordinator.Write(Path.Combine(ProtocolRoot, "provider-binding-v1.json"), new ProfileLoginBinding
            {
                ProfileId = "profile-1",
            });
        }

        public void WriteRequest(string profileId = "profile-1", DateTimeOffset? minimumProcessStart = null,
            DateTimeOffset? submissionExpiry = null, DateTimeOffset? completionExpiry = null) =>
            ProfileLoginCoordinator.Write(Path.Combine(OperationRoot, "request.json"), new ProfileLoginRequest
            {
                OperationId = "operation-1",
                ProfileId = profileId,
                CharacterId = "character-1",
                CharacterName = "Eriana Ning",
                HomeWorld = "Siren",
                RequestedAtUtc = Now,
                InitialSubmissionExpiresAtUtc = submissionExpiry ?? Now.AddMinutes(5),
                CompletionExpiresAtUtc = completionExpiry ?? Now.AddMinutes(20),
                MinimumGameProcessStartUtc = minimumProcessStart ?? Now.AddSeconds(-10),
            });

        public void WriteReceipt(string phase) => ProfileLoginCoordinator.Write(Path.Combine(OperationRoot, "receipt.json"), new ProfileLoginReceipt
        {
            OperationId = "operation-1", ProfileId = "profile-1", Sequence = 1, Phase = phase, UpdatedAtUtc = Now, ProcessId = 42,
        });
        public void WriteRawReceipt(string value)
        {
            Directory.CreateDirectory(OperationRoot);
            File.WriteAllText(Path.Combine(OperationRoot, "receipt.json"), value);
        }

        public void Tick(TimeSpan? advance = null)
        {
            clock += advance ?? TimeSpan.Zero;
            new ProfileLoginCoordinator(root, Driver, () => identity, (_, _) => { }, () => clock, () => CurrentProcessId, () => processStartedAt).TickCore(clock);
        }

        public ProfileLoginReceipt ReadReceipt() => JsonSerializer.Deserialize<ProfileLoginReceipt>(File.ReadAllText(Path.Combine(OperationRoot, "receipt.json")))!;
        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class FakeDriver : ICharacterLoginDriver
    {
        public Queue<LifestreamLoginSubmissionResult> Results { get; } = new();
        public int Calls { get; private set; }
        public LifestreamLoginSubmissionResult TryBegin(string characterName, string homeWorld)
        {
            Calls++;
            return Results.Count > 0 ? Results.Dequeue() : new(true, "Submitted", "accepted", "TitleScreen");
        }
    }
}
