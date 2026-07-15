namespace MarketMafioso.Tests;

public sealed class ReceiverEndpointClassifierTests
{
    [Fact]
    public void KnownHostedCanonicalEndpointDerivesDashboardAndAcquisitionApi()
    {
        const string serverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory";

        var endpoint = ReceiverEndpointClassifier.Classify(serverUrl);

        Assert.Equal(ReceiverEndpointKind.KnownHosted, endpoint.Kind);
        Assert.Equal("https://dev.xivcraftarchitect.com/marketmafioso", endpoint.DashboardBaseUrl);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/",
            ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl));
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/settings?tab=authentication",
            ReceiverEndpointClassifier.BuildClientKeyManagerUrl(serverUrl));
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition",
            ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl));
    }

    [Fact]
    public void RetiredHostedApiMarketMafiosoEndpointIsInvalid()
    {
        const string serverUrl = "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory";

        var endpoint = ReceiverEndpointClassifier.Classify(serverUrl);

        Assert.Equal(ReceiverEndpointKind.Invalid, endpoint.Kind);
        Assert.Null(ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl));
        Assert.Null(ReceiverEndpointClassifier.BuildClientKeyManagerUrl(serverUrl));
        Assert.Null(ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl));
    }

    [Fact]
    public void LocalInventoryEndpointKeepsRootDashboardAndAcquisitionRoutes()
    {
        const string serverUrl = "http://localhost:8080/inventory";

        Assert.Equal(
            "http://localhost:8080/",
            ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl));
        Assert.Equal(
            "http://localhost:8080/settings?tab=authentication",
            ReceiverEndpointClassifier.BuildClientKeyManagerUrl(serverUrl));
        Assert.Equal(
            "http://localhost:8080/acquisition",
            ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl));
    }
}
