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
        Assert.DoesNotContain("GetNativePresentationState", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketOverlayUsesControllerOwnedSessionInsteadOfTransientAddonVisibility()
    {
        var overlay = ReadSource("src", "MarketMafioso", "Windows", "RemoteMarketOverlayWindow.cs");
        var controller = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");
        var drawConditions = ExtractMethodBody(overlay, "DrawConditions");

        Assert.Contains("controller.ShouldPresentOverlay()", drawConditions, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMarketBoardResultVisible()", drawConditions, StringComparison.Ordinal);
        Assert.Contains("RemoteMarketOverlaySession overlaySession", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketConfirmedPurchaseReconcilesInPlace()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");
        var confirmation = ExtractMethodBody(source, "OnItemPurchased");
        var completion = ExtractMethodBody(source, "Complete");

        Assert.Contains("purchasedListingIds.Add(pending.Selection.ListingId)", confirmation, StringComparison.Ordinal);
        Assert.Contains("ReconcileConfirmedPurchase(pending.Selection.ListingId)", confirmation, StringComparison.Ordinal);
        Assert.Contains("framework.RunOnTick(AdvanceBatch, BatchPacingDelay)", completion, StringComparison.Ordinal);
        Assert.Contains("proxy->SetLastPurchasedItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginPostPurchaseRefresh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingPostPurchaseRefresh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireFreshBrowse", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketOverlayExposesItsRealPurchaseStepsForReviewedAutomation()
    {
        var overlay = ReadSource("src", "MarketMafioso", "Windows", "RemoteMarketOverlayWindow.cs");
        var bridge = ReadSource("src", "MarketMafioso", "AgentBridge", "MarketMafiosoBridgeProvider.cs");

        Assert.Contains("\"remote-market.select-cheapest\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"remote-market.arm-purchase\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"remote-market.confirm-purchase\"", overlay, StringComparison.Ordinal);
        Assert.Contains("surfaceId: \"remote-market\"", overlay, StringComparison.Ordinal);
        Assert.Contains("new(\"remote-market\", \"Remote Market Overlay\"", bridge, StringComparison.Ordinal);
        Assert.Contains("reviewRegistry.ActionCatalog()", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketListingCacheRefreshesFromNativeLifecycleEvents()
    {
        var source = ReadSource(
            "src",
            "MarketMafioso",
            "MarketAcquisition",
            "RemoteMarket",
            "RemoteMarketNativeListingCache.cs");

        Assert.Contains(
            "RegisterListener(AddonEvent.PostRefresh, ItemSearchResultAddon, OnNativeListingsChanged)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("private void OnNativeListingsChanged", source, StringComparison.Ordinal);
        Assert.Contains("marketBoard.OfferingsReceived += OnOfferingsReceived", source, StringComparison.Ordinal);
        Assert.Contains("framework.RunOnTick(Capture)", source, StringComparison.Ordinal);
        Assert.Contains("SnapshotChanged?.Invoke(candidate)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketControllerConsumesNativeListingCapability()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");

        Assert.Contains("RemoteMarketNativeListingCache nativeListingCache", source, StringComparison.Ordinal);
        Assert.Contains("nativeListingCache.SnapshotChanged += OnNativeListingSnapshotChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterListener(AddonEvent.PostRefresh", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMarketPurchaseAutomaticallyRepairsUnverifiedNativeEntry()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "RemoteMarket", "RemoteMarketController.cs");
        var beginBatch = ExtractMethodBody(source, "BeginBatch");

        Assert.Contains("RequiresAutomaticPurchaseVerification(", beginBatch, StringComparison.Ordinal);
        Assert.Contains("BeginPendingPurchaseVerification(", beginBatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Open this item through MMF", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Select the listing in MMF", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-search to refresh", source, StringComparison.Ordinal);
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
