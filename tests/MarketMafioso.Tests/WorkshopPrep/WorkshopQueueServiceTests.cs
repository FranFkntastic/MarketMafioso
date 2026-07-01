using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopQueueServiceTests
{
    [Fact]
    public void FreezeCurrentQueue_creates_deep_copy_and_marks_active_frozen_queue()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
            ],
        };
        var now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);

        var result = WorkshopQueueService.FreezeCurrentQueue(config, "Shark parts", now);

        Assert.True(result.Success);
        Assert.Single(config.FrozenWorkshopQueues);
        Assert.Equal(config.FrozenWorkshopQueues[0].Id, config.ActiveFrozenWorkshopQueueId);
        Assert.Equal("Shark parts", config.FrozenWorkshopQueues[0].Name);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].CreatedAt);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
        Assert.Equal(531u, config.FrozenWorkshopQueues[0].Items[0].WorkshopItemId);
        Assert.NotSame(config.WorkshopPrepQueue[0], config.FrozenWorkshopQueues[0].Items[0]);
    }

    [Fact]
    public void LoadFrozenQueue_replaces_active_queue_with_deep_copy()
    {
        var frozenId = Guid.NewGuid();
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 111, Quantity = 1 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Name = "Load me",
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
                    ],
                },
            ],
        };

        var result = WorkshopQueueService.LoadFrozenQueue(config, frozenId);

        Assert.True(result.Success);
        Assert.Equal(frozenId, config.ActiveFrozenWorkshopQueueId);
        Assert.Single(config.WorkshopPrepQueue);
        Assert.Equal(531u, config.WorkshopPrepQueue[0].WorkshopItemId);
        Assert.NotSame(config.FrozenWorkshopQueues[0].Items[0], config.WorkshopPrepQueue[0]);
    }

    [Fact]
    public void FreezeCurrentQueue_rejects_duplicate_names_case_insensitively()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue { Name = "Shark Parts" },
            ],
        };

        var result = WorkshopQueueService.FreezeCurrentQueue(config, "shark parts", DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveActiveQueue_creates_frozen_queue_when_active_queue_is_unsaved()
    {
        var now = new DateTime(2026, 6, 24, 16, 0, 0, DateTimeKind.Utc);
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
        };

        var result = WorkshopQueueService.SaveActiveQueue(config, "Bridge batch", now);

        Assert.True(result.Success);
        Assert.Single(config.FrozenWorkshopQueues);
        Assert.Equal("Bridge batch", config.FrozenWorkshopQueues[0].Name);
        Assert.Equal(config.FrozenWorkshopQueues[0].Id, config.ActiveFrozenWorkshopQueueId);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].CreatedAt);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
        Assert.NotSame(config.WorkshopPrepQueue[0], config.FrozenWorkshopQueues[0].Items[0]);
    }

    [Fact]
    public void SaveActiveQueue_overwrites_active_frozen_queue_when_linked()
    {
        var frozenId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 24, 16, 30, 0, DateTimeKind.Utc);
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = frozenId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 532, Quantity = 4 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Name = "Bridge batch",
                    CreatedAt = now.AddDays(-1),
                    UpdatedAt = now.AddDays(-1),
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
                    ],
                },
            ],
        };

        var result = WorkshopQueueService.SaveActiveQueue(config, "Ignored because linked", now);

        Assert.True(result.Success);
        Assert.Single(config.FrozenWorkshopQueues);
        Assert.Equal("Bridge batch", config.FrozenWorkshopQueues[0].Name);
        Assert.Equal(532u, config.FrozenWorkshopQueues[0].Items[0].WorkshopItemId);
        Assert.Equal(4, config.FrozenWorkshopQueues[0].Items[0].Quantity);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
    }

    [Fact]
    public void SaveActiveQueue_requires_name_for_unsaved_queue()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
        };

        var result = WorkshopQueueService.SaveActiveQueue(config, " ", DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.Empty(config.FrozenWorkshopQueues);
        Assert.Null(config.ActiveFrozenWorkshopQueueId);
    }

    [Fact]
    public void GetFrozenQueueState_returns_loaded_for_active_matching_queue()
    {
        var frozenId = Guid.NewGuid();
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = frozenId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
                    ],
                },
            ],
        };

        var state = WorkshopQueueService.GetFrozenQueueState(config, frozenId);

        Assert.Equal(WorkshopFrozenQueueState.Loaded, state);
    }

    [Fact]
    public void GetFrozenQueueState_returns_modified_for_active_diverging_queue()
    {
        var frozenId = Guid.NewGuid();
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = frozenId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 3 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
                    ],
                },
            ],
        };

        var state = WorkshopQueueService.GetFrozenQueueState(config, frozenId);

        Assert.Equal(WorkshopFrozenQueueState.Modified, state);
    }

    [Fact]
    public void GetFrozenQueueState_returns_not_loaded_for_other_queue()
    {
        var activeId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = activeId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = activeId,
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
                    ],
                },
                new WorkshopFrozenQueue
                {
                    Id = otherId,
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 532, Quantity = 1 },
                    ],
                },
            ],
        };

        var state = WorkshopQueueService.GetFrozenQueueState(config, otherId);

        Assert.Equal(WorkshopFrozenQueueState.NotLoaded, state);
    }

    [Fact]
    public void OverwriteFrozenQueue_replaces_items_and_updates_timestamp()
    {
        var frozenId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 24, 13, 0, 0, DateTimeKind.Utc);
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = frozenId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 532, Quantity = 4 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Name = "Queue",
                    UpdatedAt = now.AddDays(-1),
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
                    ],
                },
            ],
        };

        var result = WorkshopQueueService.OverwriteFrozenQueue(config, frozenId, now);

        Assert.True(result.Success);
        Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
        Assert.Equal(532u, config.FrozenWorkshopQueues[0].Items[0].WorkshopItemId);
        Assert.Equal(4, config.FrozenWorkshopQueues[0].Items[0].Quantity);
        Assert.NotSame(config.WorkshopPrepQueue[0], config.FrozenWorkshopQueues[0].Items[0]);
    }

    [Fact]
    public void RenameFrozenQueue_rejects_duplicate_names_case_insensitively()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var config = new Configuration
        {
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue { Id = first, Name = "Alpha" },
                new WorkshopFrozenQueue { Id = second, Name = "Beta" },
            ],
        };

        var result = WorkshopQueueService.RenameFrozenQueue(config, second, "alpha", DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.Equal("Beta", config.FrozenWorkshopQueues[1].Name);
    }

    [Fact]
    public void NewActiveQueue_clears_active_items_and_frozen_link()
    {
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = Guid.NewGuid(),
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
            ],
        };

        WorkshopQueueService.NewActiveQueue(config);

        Assert.Empty(config.WorkshopPrepQueue);
        Assert.Null(config.ActiveFrozenWorkshopQueueId);
    }

    [Fact]
    public void MarkActiveQueueEdited_preserves_frozen_link_when_queue_diverges()
    {
        var frozenId = Guid.NewGuid();
        var config = new Configuration
        {
            ActiveFrozenWorkshopQueueId = frozenId,
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 15 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
                    ],
                },
            ],
        };

        WorkshopQueueService.MarkActiveQueueEdited(config);

        Assert.Equal(frozenId, config.ActiveFrozenWorkshopQueueId);
        Assert.Equal(16, config.FrozenWorkshopQueues[0].Items[0].Quantity);
    }

    [Fact]
    public void DecrementActiveQueue_removes_row_when_quantity_reaches_zero()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
            ],
        };

        var result = WorkshopQueueService.DecrementActiveQueue(config, 531);

        Assert.True(result.Success);
        Assert.Empty(config.WorkshopPrepQueue);
    }

    [Fact]
    public void DecrementActiveQueue_does_not_mutate_frozen_queue()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Name = "Original",
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
                    ],
                },
            ],
        };

        WorkshopQueueService.DecrementActiveQueue(config, 531);

        Assert.Equal(1, config.WorkshopPrepQueue[0].Quantity);
        Assert.Equal(2, config.FrozenWorkshopQueues[0].Items[0].Quantity);
    }
}
