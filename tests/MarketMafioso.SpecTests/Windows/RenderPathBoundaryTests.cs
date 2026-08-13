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
    public void MarketListingViewGetterOnlyReturnsThePublishedSnapshot()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");
        var declaration = "public MarketListingView GetView() => cachedView;";

        Assert.Contains(declaration, source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketListingOverlayVisibilityRequiresCurrentSnapshotIdentity()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");
        var body = ExtractMethodBody(source, "IsMarketBoardResultVisible");

        Assert.Contains("listingSession.IsCurrentNativePresentation(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNativePresentationState", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketListingOverlayUsesControllerOwnedSessionInsteadOfTransientAddonVisibility()
    {
        var overlay = ReadSource("src", "MarketMafioso", "Windows", "MarketListingOverlayWindow.cs");
        var controller = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");
        var drawConditions = ExtractMethodBody(overlay, "DrawConditions");

        Assert.Contains("controller.ShouldPresentOverlay()", drawConditions, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMarketBoardResultVisible()", drawConditions, StringComparison.Ordinal);
        Assert.Contains("MarketListingPresentationSession presentationSession", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketListingOverlayIsNotForcedOpenAndOnlyDrawsVisibleRows()
    {
        var plugin = ReadSource("src", "MarketMafioso", "Plugin.cs");
        var overlay = ReadSource("src", "MarketMafioso", "Windows", "MarketListingOverlayWindow.cs");
        var frameworkUpdate = ExtractMethodBody(plugin, "OnFrameworkUpdate");
        var drawListings = ExtractMethodBody(overlay, "DrawListingsTable");
        var synchronizeLifetime = ExtractMethodBody(overlay, "SynchronizePresentationLifetime");

        Assert.DoesNotContain("MarketListingOverlay.IsOpen = true", frameworkUpdate, StringComparison.Ordinal);
        Assert.Contains("MarketListingOverlay.SynchronizePresentationLifetime()", frameworkUpdate, StringComparison.Ordinal);
        Assert.Contains("if (active && !presentationActive)", synchronizeLifetime, StringComparison.Ordinal);
        Assert.Contains("tableProjection.DrawClippedRows(", drawListings, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (", drawListings, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedListingPurchaseReconcilesInPlace()
    {
        var coordinator = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketListingPurchaseCoordinator.cs");
        var controller = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");
        var confirmation = ExtractMethodBody(coordinator, "OnItemPurchased");
        var completion = ExtractMethodBody(coordinator, "Complete");

        Assert.Contains("purchasedListingIds.Add(pending.Selection.ListingId)", confirmation, StringComparison.Ordinal);
        Assert.Contains("reconcileConfirmedPurchase(pending.Selection.ListingId)", confirmation, StringComparison.Ordinal);
        Assert.Contains("listingSession.ConfirmPurchase(listingId)", controller, StringComparison.Ordinal);
        Assert.Contains("framework.RunOnTick(Advance, NextBatchPacingDelay())", completion, StringComparison.Ordinal);
        Assert.Contains("MinimumBatchPacingMilliseconds = 1300", coordinator, StringComparison.Ordinal);
        Assert.Contains("MaximumBatchPacingMilliseconds = 1900", coordinator, StringComparison.Ordinal);
        Assert.Contains("proxy->SetLastPurchasedItem", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginPostPurchaseRefresh", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingPostPurchaseRefresh", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireFreshBrowse", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketListingOverlayExposesItsRealPurchaseStepsForReviewedAutomation()
    {
        var overlay = ReadSource("src", "MarketMafioso", "Windows", "MarketListingOverlayWindow.cs");
        var bridge = ReadSource("src", "MarketMafioso", "AgentBridge", "MarketMafiosoBridgeProvider.cs");

        Assert.Contains("\"market-listings.select-cheapest\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"market-listings.arm-purchase\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"market-listings.confirm-purchase\"", overlay, StringComparison.Ordinal);
        Assert.Contains("surfaceId: \"market-listings\"", overlay, StringComparison.Ordinal);
        Assert.Contains("new(\"market-listings\", \"Market Listings\"", bridge, StringComparison.Ordinal);
        Assert.Contains("reviewRegistry.ActionCatalog()", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchRecoveryExposesRestorePreviousAsAReviewedAction()
    {
        var source = ReadSource("src", "MarketMafioso", "Windows", "MainWindow.cs");
        var toolbar = ExtractMethodBody(source, "DrawMarketAcquisitionWorkbenchToolbar");

        Assert.Contains("\"acquisition.recovery.restore-previous-workbench\"", toolbar, StringComparison.Ordinal);
        Assert.Contains("canMutate && acquisitionRequestBuilder.HasPreviousWorkbench", toolbar, StringComparison.Ordinal);
        Assert.Contains("acquisitionRequestBuilder.RestorePreviousWorkbench", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteTravelPreflightClosesTheOwnedItemSearchAgentAsWellAsItsWindows()
    {
        var source = ReadSource(
            "src",
            "MarketMafioso",
            "MarketAcquisition",
            "DalamudMarketAcquisitionRouteEngineAdapters.cs");
        var close = ExtractMethodBody(source, "TryCloseMarketBoardWindows");

        Assert.Contains("closeOwnedMarketBoardForTravel()", close, StringComparison.Ordinal);
        Assert.Contains("GetAgentByInternalId(AgentId.ItemSearch)", close, StringComparison.Ordinal);
        Assert.Contains("itemSearchAgent->Hide()", close, StringComparison.Ordinal);
        Assert.Contains("TryCloseAddon(\"ItemSearchResult\")", close, StringComparison.Ordinal);
        Assert.Contains("TryCloseAddon(\"ItemSearch\")", close, StringComparison.Ordinal);

        var engine = ReadSource(
            "src",
            "MarketMafioso",
            "MarketAcquisition",
            "MarketAcquisitionRouteEngine.cs");
        var pendingStop = ExtractMethodBody(engine, "HandlePendingStop");
        Assert.Contains(
            "requiresTravelPreparation && uiAutomation.TryCloseMarketBoardWindows()",
            pendingStop,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerConsumesSharedEventDrivenListingObserver()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");

        Assert.Contains("DalamudMarketBoardListingObserver listingObserver", source, StringComparison.Ordinal);
        Assert.Contains("listingObserver.Changed += OnListingObservationChanged", source, StringComparison.Ordinal);
        Assert.Contains("MarketBoardListingSession listingSession", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterListener(AddonEvent.PostRefresh", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ListingPurchaseAutomaticallyRepairsUnverifiedNativeEntry()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketBoard", "MarketBoardAcquisitionController.cs");
        var beginBatch = ExtractMethodBody(source, "BeginBatch");

        Assert.Contains("listingSession.IsVerifiedForPurchase(", beginBatch, StringComparison.Ordinal);
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
    public void MarketListingDrawMethodsOnlyRenderCachedState(string methodName)
    {
        var source = ReadSource("src", "MarketMafioso", "Windows", "MarketListingOverlayWindow.cs");
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
    public void MarketListingIconCacheDoesNotRetainAFrameScopedTextureWrap()
    {
        var source = ReadSource("src", "MarketMafioso", "Windows", "MarketListingOverlayWindow.cs");
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
