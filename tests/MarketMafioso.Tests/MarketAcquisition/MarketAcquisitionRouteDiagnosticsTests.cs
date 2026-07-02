namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsTests
{
    [Fact]
    public void CreateEnabled_CreatesTimestampedRouteLog()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 25, 22, 45, 12, TimeSpan.Zero);

        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            startedAt);

        Assert.True(diagnostics.IsEnabled);
        Assert.NotNull(diagnostics.FilePath);
        Assert.StartsWith(directory, diagnostics.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("route-20260625-224512.log", diagnostics.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.FilePath));
        Assert.EndsWith("observed-listings-20260625-224512.csv", diagnostics.ObservedListingsCsvPath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.ObservedListingsCsvPath));
        Assert.EndsWith("purchase-records-20260625-224512.csv", diagnostics.PurchaseRecordsCsvPath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.PurchaseRecordsCsvPath));
    }

    [Fact]
    public void CreateInputCapture_CreatesTimestampedInputCaptureLog()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 25, 22, 45, 12, TimeSpan.Zero);

        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateInputCapture(
            directory,
            startedAt);

        Assert.True(diagnostics.IsEnabled);
        Assert.NotNull(diagnostics.FilePath);
        Assert.StartsWith(directory, diagnostics.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("input-capture-20260625-224512.log", diagnostics.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.FilePath));
        Assert.Null(diagnostics.ObservedListingsCsvPath);
        Assert.Null(diagnostics.PurchaseRecordsCsvPath);
    }

    [Fact]
    public void Record_WritesEventDetails()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.Record(
            "travel-command",
            "Sent Lifestream command.",
            new Dictionary<string, string?>
            {
                ["world"] = "Maduin",
                ["command"] = "/li Maduin mb",
                ["empty"] = null,
            });

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("travel-command", text, StringComparison.Ordinal);
        Assert.Contains("Sent Lifestream command.", text, StringComparison.Ordinal);
        Assert.Contains("world: Maduin", text, StringComparison.Ordinal);
        Assert.Contains("command: /li Maduin mb", text, StringComparison.Ordinal);
        Assert.DoesNotContain("empty:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_WritesCompletionEvent()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.Complete("Route complete.");

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("complete", text, StringComparison.Ordinal);
        Assert.Contains("Route complete.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAutomationSnapshot_WritesClassifiedState()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.RecordAutomationSnapshot(
            MarketMafioso.Automation.MarketBoard.MarketBoardAutomationSnapshot.Create(
                "SearchItem",
                "AfterInput",
                "ItemSearchResultReady",
                "SearchSent",
                MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.Recoverable,
                "RetryHumanEnterPath",
                new Dictionary<string, string?>
                {
                    ["searchText"] = "Varnish",
                }));

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("automation-snapshot", text, StringComparison.Ordinal);
        Assert.Contains("step: SearchItem", text, StringComparison.Ordinal);
        Assert.Contains("phase: AfterInput", text, StringComparison.Ordinal);
        Assert.Contains("expected: ItemSearchResultReady", text, StringComparison.Ordinal);
        Assert.Contains("observed: SearchSent", text, StringComparison.Ordinal);
        Assert.Contains("outcome: Recoverable", text, StringComparison.Ordinal);
        Assert.Contains("nextAction: RetryHumanEnterPath", text, StringComparison.Ordinal);
        Assert.Contains("searchText: Varnish", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordObservedListings_WritesCandidateRowsToCsv()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.RecordObservedListings(
            "request-1",
            "Coeurl",
            "Crystal",
            new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
            {
                LineId = "line-1",
                LineOrdinal = 2,
                Source = "Planned",
                ItemId = 5121,
                ItemName = "Darksteel Ore",
            },
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Status = "Ready",
                Message = "Would buy this row.",
                ReadableListingCount = 1,
                ReportedListingCount = 32,
                ListingCapacity = 100,
                IsVisibleListingCacheTruncated = false,
                RequestedQuantity = 999,
                WouldBuyQuantity = 55,
                WouldSpendGil = 30140,
                Rows =
                [
                    new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow
                    {
                        Decision = "WouldBuy",
                        Reason = "SafeLiveCandidate",
                        Message = "Below threshold.",
                        RunningQuantityAfter = 55,
                        RunningGilAfter = 30140,
                        LiveListing = new MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing
                        {
                            ItemId = 5121,
                            RawItemId = 5121,
                            WorldName = "Coeurl",
                            ListingId = "listing-1",
                            RetainerId = "retainer-1",
                            RetainerName = "Eth",
                            UnitPrice = 548,
                            Quantity = 55,
                        },
                    },
                ],
            });

        var csv = ReadLog(diagnostics.ObservedListingsCsvPath!);
        Assert.Contains("request-1,Coeurl,Crystal,line-1,2,Planned,5121,Darksteel Ore", csv, StringComparison.Ordinal);
        Assert.Contains("WouldBuy,SafeLiveCandidate,Below threshold.,5121,5121,Coeurl,listing-1,retainer-1,Eth,548,55,30140,False,55,30140", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordObservedListings_WritesZeroRowProbeSummaryToCsv()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.RecordObservedListings(
            "request-1",
            "Coeurl",
            "Crystal",
            null,
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Status = "VisibleCacheExhausted",
                Message = "No safe rows.",
                ReportedListingCount = 32,
                ListingCapacity = 100,
                IsVisibleListingCacheTruncated = true,
            });

        var csv = ReadLog(diagnostics.ObservedListingsCsvPath!);
        Assert.Contains("VisibleCacheExhausted,No safe rows.,0,32,100,True", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordPurchaseAudit_WritesPurchaseRecordsCsv()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.RecordPurchaseAudit(
            "request-1",
            "Crystal",
            "line-1",
            "Darksteel Ore",
            "Coeurl",
            "listing-1",
            "retainer-1",
            55,
            30140,
            "Purchased",
            "Planned",
            5121,
            "Ready");

        var csv = ReadLog(diagnostics.PurchaseRecordsCsvPath!);
        Assert.Contains("requestId,world,dataCenter,lineId,itemId,itemName,source,sourceCandidateStatus,event,result", csv, StringComparison.Ordinal);
        Assert.Contains("request-1,Coeurl,Crystal,line-1,5121,Darksteel Ore,Planned,Ready,purchase-audit,Purchased,listing-1,retainer-1,55,30140,548", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_IgnoresRecords()
    {
        var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.Disabled;

        diagnostics.Record("ignored", "Ignored.");
        diagnostics.Complete("Ignored.");

        Assert.False(diagnostics.IsEnabled);
        Assert.Null(diagnostics.FilePath);
    }

    [Fact]
    public void RecordCsvEvents_after_dispose_is_noop()
    {
        var directory = CreateTempDirectory();
        var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.Dispose();

        diagnostics.RecordObservedListings(
            "request-1",
            "Coeurl",
            "Crystal",
            null,
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan());
        diagnostics.RecordPurchaseAudit(
            "request-1",
            "Crystal",
            "line-1",
            "Darksteel Ore",
            "Coeurl",
            "listing-1",
            "retainer-1",
            1,
            100,
            "Skipped",
            "Planned",
            5121,
            "Ready");
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadLog(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

