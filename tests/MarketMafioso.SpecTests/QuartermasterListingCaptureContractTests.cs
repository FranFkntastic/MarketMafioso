using System.Text.Json;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.SpecTests;

public sealed class QuartermasterListingCaptureContractTests
{
    [Fact]
    public void Client_reads_explicit_listing_capture_and_changed_kind()
    {
        var adapter = new FakeAdapter
        {
            CapabilitiesJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.CapabilitiesSchema,
                providerInstanceId = "provider-1",
                revision = 1,
            }),
            SnapshotJson = JsonSerializer.Serialize(new
            {
                schema = QuartermasterIpcClient.SnapshotSchema,
                providerInstanceId = "provider-1",
                revision = 1,
                generatedAtUtc = "2026-07-31T17:30:00Z",
                owner = new
                {
                    localContentId = 10,
                    homeWorldId = 57,
                    characterName = "Recipient",
                    homeWorldName = "Siren",
                },
                retainers = Array.Empty<object>(),
                latestRetainerListingCapture = new
                {
                    captureId = "capture-1",
                    retainerId = 20,
                    capturedAtUtc = "2026-07-31T17:29:59Z",
                    items = new[] { new { itemId = 100, itemName = "Iron Ore" } },
                },
            }),
        };
        using var client = new QuartermasterIpcClient(adapter);
        QuartermasterChanged? changed = null;
        client.Changed += value => changed = value;

        Assert.True(client.TryGetSnapshot(out var snapshot, out var error), error);
        var capture = Assert.IsType<QuartermasterRetainerListingCapture>(snapshot!.LatestRetainerListingCapture);
        Assert.Equal("capture-1", capture.CaptureId);
        Assert.Equal((uint)100, Assert.Single(capture.Items).ItemId);

        adapter.RaiseChanged(JsonSerializer.Serialize(new
        {
            schema = QuartermasterIpcClient.ChangedSchema,
            providerInstanceId = "provider-1",
            revision = 2,
            kind = "retainer_listings",
        }));

        Assert.Equal("retainer_listings", changed!.Kind);
    }

    private sealed class FakeAdapter : IQuartermasterIpcAdapter
    {
        private Action<string>? changed;
        public string CapabilitiesJson { get; init; } = string.Empty;
        public string SnapshotJson { get; init; } = string.Empty;
        public bool HasCapabilities => true;
        public bool HasSnapshot => true;
        public bool HasSubmitShortages => false;
        public bool HasSubmitElementalDeposit => false;
        public bool HasOperation => false;
        public string GetCapabilities() => CapabilitiesJson;
        public string GetSnapshot() => SnapshotJson;
        public string SubmitShortages(string requestJson) => throw new NotSupportedException();
        public string SubmitElementalDeposit(string requestJson) => throw new NotSupportedException();
        public string GetOperation(string operationId) => throw new NotSupportedException();
        public void SubscribeChanged(Action<string> handler) => changed += handler;
        public void UnsubscribeChanged(Action<string> handler) => changed -= handler;
        public void RaiseChanged(string json) => changed?.Invoke(json);
    }
}
