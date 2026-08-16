using MarketMafioso.Contracts.MarketIntelligence;

namespace MarketMafioso.Server.MarketIntelligence;

internal static class MarketEvidenceSourceRegistry
{
    public const string Version = "market-evidence-sources-v3";
    private static readonly IReadOnlyDictionary<string, MarketEvidenceSourceView> Definitions =
        new Dictionary<string, MarketEvidenceSourceView>(StringComparer.Ordinal)
        {
            [MarketEvidenceSources.MarketAcquisition] = Source(MarketEvidenceSources.MarketAcquisition,
                "DetailedListings", "StableListingIds", "WorldScopedSellerIds", "SellerNames", "SellerNameProvenance", "ArtisanContentIds", "OrderedPriceBook", "DeclaredCapacity", "CompleteReadEvidence"),
            [MarketEvidenceSources.PassiveMarketBoard] = Source(MarketEvidenceSources.PassiveMarketBoard,
                "DetailedListings", "StableListingIds", "WorldScopedSellerIds", "SellerNames", "SellerNameProvenance", "ArtisanContentIdsWhenCorrelated", "OrderedPriceBook", "DeclaredCapacity", "CompleteReadEvidence"),
            [MarketEvidenceSources.LegacyRouteImport] = Source(MarketEvidenceSources.LegacyRouteImport,
                "DetailedListings", "StableListingIds", "WorldScopedSellerIds", "SellerNamesWhenCaptured", "OrderedPriceBook", "DeclaredCapacityWhenCaptured", "CompleteReadEvidenceWhenCaptured"),
            [MarketEvidenceSources.Universalis] = Source(MarketEvidenceSources.Universalis,
                "DetailedListings", "StableListingIdsWhenReported", "WorldScopedSellerIdsWhenReported", "SellerNamesWhenReported", "ExternalFreshness", "AggregateContext"),
            [MarketEvidenceSources.SaddlebagExchange] = Source(MarketEvidenceSources.SaddlebagExchange,
                "ExternalFreshness", "AggregateContext"),
        };

    public static IReadOnlyList<MarketEvidenceSourceView> All => Definitions.Values.OrderBy(x => x.SourceKind).ToArray();
    public static MarketEvidenceSourceRegistryView View => new() { RegistryVersion = Version, Sources = All };
    public static bool TryGet(string sourceKind, out MarketEvidenceSourceView source) => Definitions.TryGetValue(sourceKind, out source!);
    public static bool Has(MarketEvidenceSourceView source, string capability) => source.Capabilities.Contains(capability, StringComparer.Ordinal);
    private static MarketEvidenceSourceView Source(string kind, params string[] capabilities) => new() { SourceKind = kind, Capabilities = capabilities };
}
