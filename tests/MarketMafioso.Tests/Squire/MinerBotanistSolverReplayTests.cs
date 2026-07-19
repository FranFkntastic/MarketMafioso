using System.IO.Compression;
using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistSolverReplayTests
{
    [Fact]
    public void SanitizedBtn72Fixture_IsTheCapturedTwelvePositionExactRequest()
    {
        var replay = ReadBtn72Fixture();

        Assert.Equal(MinerBotanistSolverReplay.CurrentSchemaVersion, replay.SchemaVersion);
        Assert.Equal(MinerBotanistUtilityProfile.BotanistClassJobId, replay.Profile.ClassJobId);
        Assert.Equal((uint)72, replay.Profile.CharacterLevel);
        Assert.Equal(12, replay.RequiredPositions.Count);
        Assert.Equal(260, replay.Offers.Count);
        Assert.Equal(223, replay.Offers.Count(value => value.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard));
        Assert.Equal(25, replay.Offers.Count(value => value.SourceKind == EquipmentAcquisitionSourceKind.GilVendor));
        Assert.Equal(12, replay.Offers.Count(value => value.SourceKind == EquipmentAcquisitionSourceKind.Owned));
        _ = replay.ToRequest();
    }

    [Fact]
    public void Capture_IsDeterministicSanitizedAndRoundTripsTheExactRequestShape()
    {
        var request = Request();

        var first = Capture(request);
        var reordered = Capture(request with { Offers = request.Offers.Reverse().ToArray() });
        var json = JsonSerializer.Serialize(first);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(reordered));
        Assert.DoesNotContain("Siren", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-observation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("market:live", json, StringComparison.Ordinal);
        var recaptured = MinerBotanistSolverReplay.Capture(
            first.ToRequest(),
            first.Profile.Context,
            first.Profile.ClassJobId,
            first.Profile.CharacterLevel,
            first.Profile.OfferBaseline,
            first.Profile.FixedStats);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(recaptured));
    }

    [Fact]
    public void Replay_PreservesReferenceFrontierMetricsAndExactVariantCounts()
    {
        var request = Request();
        var replay = Capture(request);

        var expected = new EquipmentExactFrontierSolver().Solve(request);
        var actual = new EquipmentExactFrontierSolver().Solve(replay.ToRequest());

        Assert.Equal(Metrics(expected), Metrics(actual));
        Assert.Equal(expected.Diagnostics.ExactCompleteVariantCount, actual.Diagnostics.ExactCompleteVariantCount);
        Assert.Equal(expected.EquivalenceSummaries.Select(value => value.ExactVariantCount).Order().ToArray(),
            actual.EquivalenceSummaries.Select(value => value.ExactVariantCount).Order().ToArray());
    }

    private static MinerBotanistSolverReplay Capture(EquipmentExactFrontierRequest request) =>
        MinerBotanistSolverReplay.Capture(
            request,
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            MinerBotanistUtilityProfile.BotanistClassJobId,
            72,
            new(900, 900, 600),
            new(50, 50, 50));

    internal static MinerBotanistSolverReplay ReadBtn72Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Squire", "btn72-solver-replay-v1.json.gz");
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<MinerBotanistSolverReplay>(gzip, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("BTN 72 solver replay fixture was empty.");
    }

    private static EquipmentExactFrontierRequest Request()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            new(900, 900, 600),
            MinerBotanistUtilityProfile.BotanistClassJobId,
            72,
            new(50, 50, 50));
        var head = Offer(EquipmentLoadoutPosition.Head, 100, EquipmentAcquisitionSourceKind.Owned, null, null, 10, 10, 0, 0);
        var body = Offer(EquipmentLoadoutPosition.Body, 200, EquipmentAcquisitionSourceKind.Owned, null, null, 10, 10, 0, 0);
        var upgradeA = Offer(EquipmentLoadoutPosition.Head, 101, EquipmentAcquisitionSourceKind.MarketBoard,
            "private-observation-a", "Siren", 30, 20, 0, 1_000);
        var upgradeB = Offer(EquipmentLoadoutPosition.Head, 101, EquipmentAcquisitionSourceKind.MarketBoard,
            "private-observation-b", "Siren", 30, 20, 0, 1_000);
        var vendor = Offer(EquipmentLoadoutPosition.Body, 201, EquipmentAcquisitionSourceKind.GilVendor,
            null, null, 20, 30, 0, 500, vendor: "private-vendor");
        return new(
            [head, body, upgradeA, upgradeB, vendor],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body },
            new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>
            {
                [EquipmentLoadoutPosition.Head] = head.AllocationKey,
                [EquipmentLoadoutPosition.Body] = body.AllocationKey,
            },
            profile);
    }

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        EquipmentAcquisitionSourceKind source,
        string? observation,
        string? world,
        int gathering,
        int perception,
        int gp,
        ulong cost,
        string? vendor = null)
    {
        var definition = new EquipmentItemDefinition(
            itemId,
            $"Sensitive item {itemId}",
            1,
            1,
            position == EquipmentLoadoutPosition.Head ? EquipmentSlot.Head : EquipmentSlot.Body,
            new HashSet<uint> { MinerBotanistUtilityProfile.BotanistClassJobId },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var offer = new EquipmentLoadoutOffer(
            definition,
            source,
            "Sensitive source label",
            cost > uint.MaxValue ? uint.MaxValue : (uint)cost,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: $"market:live:{source}:{itemId}");
        return new(
            offer,
            observation,
            new HashSet<EquipmentLoadoutPosition> { position },
            1,
            new([
                new("gathering", gathering),
                new("perception", perception),
                new("gathering-points", gp),
            ]),
            cost,
            world,
            vendor,
            source == EquipmentAcquisitionSourceKind.Owned ? 0 : 1,
            new(0, 0, 0),
            [source.ToString()]);
    }

    private static string[] Metrics(EquipmentExactFrontierResult result) => result.Pareto.Frontier
        .Select(value => string.Join('|',
            value.AcquisitionCostGil,
            value.Utility.UtilityScore,
            value.Burden.WorldVisits,
            value.Burden.VendorStops,
            value.Burden.PurchaseTransactions,
            value.EvidenceRisk.FreshnessBucket,
            value.EvidenceRisk.IncompleteCoverageCount,
            value.EvidenceRisk.ConfidencePenalty))
        .Order(StringComparer.Ordinal)
        .ToArray();
}
