namespace MarketMafioso.Tests;

public sealed class PluginBuildInfoTests
{
    [Fact]
    public void FormatDisplayVersion_prefers_informational_version()
    {
        var result = PluginBuildInfo.FormatDisplayVersion(
            "1.0.0.0+local-dev.4c44487",
            "1.0.0.0");

        Assert.Equal("1.0.0.0+local-dev.4c44487", result);
    }

    [Fact]
    public void FormatDisplayVersion_falls_back_to_assembly_version()
    {
        var result = PluginBuildInfo.FormatDisplayVersion(
            null,
            "1.0.0.0");

        Assert.Equal("1.0.0.0", result);
    }

    [Fact]
    public void FormatDisplayVersion_falls_back_to_unknown()
    {
        var result = PluginBuildInfo.FormatDisplayVersion(
            " ",
            null);

        Assert.Equal("Unknown", result);
    }
}
