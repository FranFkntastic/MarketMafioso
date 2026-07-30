namespace MarketMafioso.SpecTests.Windows;

public sealed class RenderPathBoundaryTests
{
    [Fact]
    public void DrawMethodsDoNotPerformBlockingOrNetworkWork()
    {
        var sourceDirectory = Path.Combine(FindRepositoryRoot(), "src", "MarketMafioso");
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var (methodName, body) in ExtractDrawMethodBodies(source))
            {
                foreach (var forbidden in new[]
                         {
                             "GetAwaiter().GetResult()",
                             ".Wait(",
                             "Task.Run(",
                             "GetDataAsync(",
                             "HttpClient.",
                             ".GetAsync(",
                             ".SendAsync(",
                             "File.Read",
                             "File.Write",
                         })
                    Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RemoteMarketViewGetterOnlyReturnsThePublishedSnapshot()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");
        var declaration = "public RemoteMarketView GetView() => cachedView;";

        Assert.Contains(declaration, source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketOverlayVisibilityRequiresCurrentSnapshotIdentity()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");
        var body = ExtractMethodBody(source, "IsMarketBoardResultVisible");

        Assert.Contains("IsListingSnapshotCurrent(", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Draw")]
    [InlineData("DrawHeader")]
    [InlineData("DrawEconomicsStrip")]
    [InlineData("DrawListingsTable")]
    [InlineData("DrawBatch")]
    public void RemoteMarketDrawMethodsOnlyRenderCachedState(string methodName)
    {
        var source = ReadSource("src", "MarketMafioso", "Windows", "RemoteMarketOverlayWindow.cs");
        var body = ExtractMethodBody(source, methodName);

        foreach (var forbidden in new[]
                 {
                     ".OrderBy(",
                     ".OrderByDescending(",
                     ".ToArray(",
                     ".ToList(",
                     "GetFromGameIcon(",
                     "InvokeFunc(",
                     "Task.Run(",
                     "GetAwaiter().GetResult()",
                 })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketIconCacheDoesNotRetainAFrameScopedTextureWrap()
    {
        var source = ReadSource("src", "MarketMafioso", "Windows", "RemoteMarketOverlayWindow.cs");
        var resolver = ExtractMethodBody(source, "ResolveItemIcon");
        var header = ExtractMethodBody(source, "DrawHeader");

        Assert.Contains("private ISharedImmediateTexture? cachedIcon;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private IDalamudTextureWrap? cachedIcon;", source, StringComparison.Ordinal);
        Assert.Contains("cachedIcon?.TryGetWrap(", header, StringComparison.Ordinal);
        Assert.Contains("GetFromGameIcon(", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWrapOrEmpty(", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWrapOrDefault(", resolver, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. segments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "MarketMafioso")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "MarketMafioso.SpecTests")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MarketMafioso repository root.");
    }

    private static IEnumerable<(string Name, string Body)> ExtractDrawMethodBodies(string source)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"\b(?:public|private|protected|internal)\s+(?:(?:static|unsafe|override|virtual|sealed|async)\s+)*[\w<>,?\[\]]+\s+(Draw\w*)\s*\(");
        foreach (System.Text.RegularExpressions.Match match in matches)
            yield return (match.Groups[1].Value, ExtractBlock(source, match.Index, match.Groups[1].Value));
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"\b(?:public|private|protected|internal)\s+(?:(?:static|unsafe|override|virtual|sealed|async)\s+)*[\w<>,?\[\]]+\s+{System.Text.RegularExpressions.Regex.Escape(methodName)}\s*\(");
        Assert.True(match.Success, $"Method '{methodName}' was not found.");
        var signature = match.Index;
        return ExtractBlock(source, signature, methodName);
    }

    private static string ExtractBlock(string source, int signature, string methodName)
    {
        var openBrace = source.IndexOf('{', signature);
        var expressionBody = source.IndexOf("=>", signature, StringComparison.Ordinal);
        if (expressionBody >= 0 && (openBrace < 0 || expressionBody < openBrace))
        {
            var semicolon = source.IndexOf(';', expressionBody);
            Assert.True(semicolon >= 0, $"Method '{methodName}' has an unterminated expression body.");
            return source[expressionBody..(semicolon + 1)];
        }
        Assert.True(openBrace >= 0, $"Method '{methodName}' has no block body.");

        var depth = 0;
        for (var index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[openBrace..(index + 1)];
        }

        throw new InvalidDataException($"Method '{methodName}' has an unterminated block.");
    }
}
