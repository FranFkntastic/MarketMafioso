using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopSavedJobShareTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Export_ThenParse_RoundTripsNameOrderAndQuantities()
    {
        var text = WorkshopSavedJobShare.Export(CreateQueue(), CreateProjects(), Now);

        var parsed = WorkshopSavedJobShare.Parse(text, CreateProjects());

        Assert.True(parsed.Success, parsed.Message);
        Assert.Equal("Shark's Fleet", parsed.Name);
        Assert.Equal([(1001u, 2), (1000u, 16)], parsed.Items.Select(x => (x.WorkshopItemId, x.Quantity)));
    }

    [Fact]
    public void Parse_AcceptsJobWrappedInChatCodeFence()
    {
        var text = "here you go\n```json\n" + WorkshopSavedJobShare.Export(CreateQueue(), CreateProjects(), Now) + "\n```";

        Assert.True(WorkshopSavedJobShare.Parse(text, CreateProjects()).Success);
    }

    [Fact]
    public void Parse_MergesRepeatedProjects()
    {
        var parsed = WorkshopSavedJobShare.Parse(
            Document("""{"workshopItemId":1000,"quantity":2},{"workshopItemId":1001,"quantity":1},{"workshopItemId":1000,"quantity":3}"""),
            CreateProjects());

        Assert.True(parsed.Success, parsed.Message);
        Assert.Equal([(1000u, 5), (1001u, 1)], parsed.Items.Select(x => (x.WorkshopItemId, x.Quantity)));
    }

    [Fact]
    public void Parse_RejectsWholeJobWhenAProjectIsUnknown()
    {
        var parsed = WorkshopSavedJobShare.Parse(
            Document("""{"workshopItemId":1000,"quantity":2},{"workshopItemId":4242,"name":"Future Hull","quantity":1}"""),
            CreateProjects());

        Assert.False(parsed.Success);
        Assert.Empty(parsed.Items);
        Assert.Contains("Future Hull (#4242)", parsed.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some chat text")]
    [InlineData("{ not json }")]
    [InlineData("""{"format":"something-else","version":1,"name":"x","items":[{"workshopItemId":1000,"quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":2,"name":"x","items":[{"workshopItemId":1000,"quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","name":"x","items":[{"workshopItemId":1000,"quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"  ","items":[{"workshopItemId":1000,"quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[{"workshopItemId":1000,"quantity":0}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[{"workshopItemId":1000,"quantity":10000}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[{"workshopItemId":-1,"quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[{"workshopItemId":"1000","quantity":1}]}""")]
    [InlineData("""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"x","items":[{"workshopItemId":1000,"quantity":9999},{"workshopItemId":1000,"quantity":1}]}""")]
    public void Parse_RejectsTextThatIsNotAValidSavedJob(string? text)
    {
        var parsed = WorkshopSavedJobShare.Parse(text, CreateProjects());

        Assert.False(parsed.Success);
        Assert.NotEmpty(parsed.Message);
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public void ImportFrozenQueue_AddsNewJobWithoutTouchingActiveQueueOrExistingJobs()
    {
        var config = new Configuration();
        config.WorkshopPrepQueue.Add(new WorkshopPrepQueueItem { WorkshopItemId = 1001, Quantity = 7 });
        var existing = CreateQueue();
        config.FrozenWorkshopQueues.Add(existing);
        config.ActiveFrozenWorkshopQueueId = existing.Id;
        var incoming = new List<WorkshopPrepQueueItem> { new() { WorkshopItemId = 1000, Quantity = 4 } };

        var first = WorkshopQueueService.ImportFrozenQueue(config, "shark's fleet", incoming, Now);
        var second = WorkshopQueueService.ImportFrozenQueue(config, "shark's fleet", incoming, Now);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(
            ["Shark's Fleet", "shark's fleet (2)", "shark's fleet (3)"],
            config.FrozenWorkshopQueues.Select(x => x.Name));
        Assert.Equal(first.QueueId, config.FrozenWorkshopQueues[1].Id);
        Assert.Equal(2, existing.Items.Count);
        Assert.Equal(existing.Id, config.ActiveFrozenWorkshopQueueId);
        Assert.Equal(7, Assert.Single(config.WorkshopPrepQueue).Quantity);

        incoming[0].Quantity = 99;
        Assert.Equal(4, config.FrozenWorkshopQueues[1].Items[0].Quantity);
    }

    [Fact]
    public void ImportSavedJob_StoresValidJobWithoutLoadingIt()
    {
        var config = new Configuration();
        config.WorkshopPrepQueue.Add(new WorkshopPrepQueueItem { WorkshopItemId = 1001, Quantity = 7 });

        var result = WorkshopQueueService.ImportSavedJob(
            config,
            WorkshopSavedJobShare.Export(CreateQueue(), CreateProjects(), Now),
            CreateProjects(),
            Now);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Shark's Fleet", result.Name);
        var saved = Assert.Single(config.FrozenWorkshopQueues);
        Assert.Equal(result.QueueId, saved.Id);
        Assert.Equal("Shark's Fleet", saved.Name);
        Assert.Equal([(1001u, 2), (1000u, 16)], saved.Items.Select(x => (x.WorkshopItemId, x.Quantity)));
        Assert.Null(config.ActiveFrozenWorkshopQueueId);
        Assert.Equal(7, Assert.Single(config.WorkshopPrepQueue).Quantity);
    }

    [Fact]
    public void ImportSavedJob_RejectsInvalidTextWithMessageAndStoresNothing()
    {
        var config = new Configuration();

        var result = WorkshopQueueService.ImportSavedJob(
            config,
            Document("""{"workshopItemId":4242,"name":"Future Hull","quantity":1}"""),
            CreateProjects(),
            Now);

        Assert.False(result.Success);
        Assert.Null(result.Name);
        Assert.Null(result.QueueId);
        Assert.Contains("Future Hull (#4242)", result.Message);
        Assert.Empty(config.FrozenWorkshopQueues);
    }

    [Fact]
    public void ImportSavedJob_RenamesOnNameCollisionAndReportsFinalName()
    {
        var config = new Configuration();
        config.FrozenWorkshopQueues.Add(CreateQueue());
        var text = WorkshopSavedJobShare.Export(CreateQueue(), CreateProjects(), Now);

        var second = WorkshopQueueService.ImportSavedJob(config, text, CreateProjects(), Now);
        var third = WorkshopQueueService.ImportSavedJob(config, text, CreateProjects(), Now);

        Assert.Equal("Shark's Fleet (2)", second.Name);
        Assert.Equal("Shark's Fleet (3)", third.Name);
        Assert.Contains("Shark's Fleet (3)", third.Message);
        Assert.Equal(
            ["Shark's Fleet", "Shark's Fleet (2)", "Shark's Fleet (3)"],
            config.FrozenWorkshopQueues.Select(x => x.Name));
    }

    private static string Document(string items) =>
        $$"""{"format":"marketmafioso.workshop-saved-job","version":1,"name":"Shared","items":[{{items}}]}""";

    private static WorkshopFrozenQueue CreateQueue() => new()
    {
        Name = "Shark's Fleet",
        Items =
        [
            new() { WorkshopItemId = 1001, Quantity = 2 },
            new() { WorkshopItemId = 1000, Quantity = 16 },
        ],
    };

    private static IReadOnlyList<WorkshopProjectDefinition> CreateProjects() =>
    [
        new(1000, 2000, "Shark-class Pressure Hull", 11, []),
        new(1001, 2001, "Shark-class Stern", 12, []),
    ];
}
