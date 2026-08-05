namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketListingArchitectureTests
{
    [Fact]
    public void AcquisitionFacadeDoesNotReabsorbBrowseOrPurchaseEventOwnership()
    {
        var facade = ReadSource("MarketBoardAcquisitionController.cs");
        var browse = ReadSource("MarketListingBrowseCoordinator.cs");
        var purchase = ReadSource("MarketListingPurchaseCoordinator.cs");
        var guard = ReadSource("MarketBoardPurchaseGuard.cs");

        Assert.DoesNotContain("marketBoard.PurchaseRequested +=", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("marketBoard.ItemPurchased +=", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("searchDriver(", facade, StringComparison.Ordinal);
        Assert.Contains("searchDriver(", browse, StringComparison.Ordinal);
        Assert.Contains("marketBoard.PurchaseRequested +=", purchase, StringComparison.Ordinal);
        Assert.Contains("marketBoard.ItemPurchased +=", purchase, StringComparison.Ordinal);
        Assert.DoesNotContain("IsRemoteSessionActive", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveRemoteOpen", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("IsRemoteSessionActive", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveRemoteOpen", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketListingModulesStayChunkyInsteadOfReformingAGodClass()
    {
        AssertFileLineCountAtMost("MarketBoardAcquisitionController.cs", 900);
        AssertFileLineCountAtMost("MarketListingBrowseCoordinator.cs", 225);
        AssertFileLineCountAtMost("MarketListingPurchaseCoordinator.cs", 525);
    }

    [Fact]
    public void MechanismNamingIsConfinedToCompatibilityChannels()
    {
        var directory = MarketBoardDirectory();
        var occurrences = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: line, Number: index + 1)))
            .Where(entry => entry.Line.Contains("RemoteMarket", StringComparison.Ordinal) ||
                            entry.Line.Contains("remote-market", StringComparison.Ordinal) ||
                            entry.Line.Contains("remote market", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.All(occurrences, occurrence =>
            Assert.True(
                occurrence.Line.Contains("MarketMafioso.OpenRemoteMarket", StringComparison.Ordinal) ||
                occurrence.Line.Contains("MarketMafioso.IsRemoteMarketAvailable", StringComparison.Ordinal),
                $"Mechanism naming escaped the compatibility boundary at {occurrence.Path}:{occurrence.Number}: {occurrence.Line.Trim()}"));
    }

    private static void AssertFileLineCountAtMost(string fileName, int maximum)
    {
        var lines = File.ReadLines(Path.Combine(MarketBoardDirectory(), fileName)).Count();
        Assert.True(lines <= maximum, $"{fileName} has {lines} lines; architectural ceiling is {maximum}.");
    }

    private static string ReadSource(string fileName)
        => File.ReadAllText(Path.Combine(MarketBoardDirectory(), fileName));

    private static string MarketBoardDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "MarketMafioso",
                "MarketAcquisition",
                "MarketBoard");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MarketMafioso market-board source directory.");
    }
}
