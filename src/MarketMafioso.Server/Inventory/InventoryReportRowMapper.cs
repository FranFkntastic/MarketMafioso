using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal static class InventoryReportRowMapper
{
    public static CharacterSummary ReadCharacter(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseDateTimeOffset(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));

    public static InventorySnapshotRow ReadSnapshot(SqliteDataReader reader) =>
        new(
            ParseDateTimeOffset(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : checked((ulong)reader.GetInt64(5)),
            reader.GetString(6),
            new InventoryReportMetadata
            {
                SchemaVersion = reader.GetInt32(7),
                SourcePlugin = reader.GetString(8),
                PluginVersion = reader.GetString(9),
                GeneratedAtUtc = reader.GetString(10),
            });

    public static InventoryOwnerRow ReadOwner(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : checked((ulong)reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : checked((ulong)reader.GetInt64(5)));

    public static ItemSlot ReadItem(SqliteDataReader reader) =>
        new()
        {
            ItemId = checked((uint)reader.GetInt64(0)),
            ItemName = reader.IsDBNull(1) ? null : reader.GetString(1),
            ItemType = reader.IsDBNull(2) ? null : reader.GetString(2),
            Quantity = checked((uint)reader.GetInt64(3)),
            IsHQ = reader.GetInt32(4) == 1,
            Condition = reader.GetFloat(5),
            ContainerKey = reader.IsDBNull(6) ? null : reader.GetString(6),
            SlotIndex = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            ConditionPercent = reader.IsDBNull(8) ? null : reader.GetFloat(8),
            Equipped = reader.IsDBNull(9) ? null : reader.GetInt32(9) == 1,
        };

    public static RetainerMarketListing ReadMarketListing(SqliteDataReader reader) =>
        new()
        {
            ItemId = checked((uint)reader.GetInt64(0)),
            ItemName = reader.IsDBNull(1) ? null : reader.GetString(1),
            ItemType = reader.IsDBNull(2) ? null : reader.GetString(2),
            Quantity = checked((uint)reader.GetInt64(3)),
            IsHQ = reader.GetInt32(4) == 1,
            Condition = reader.GetFloat(5),
            UnitPrice = reader.IsDBNull(6) ? null : checked((uint)reader.GetInt64(6)),
            ListedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
            ContainerKey = reader.IsDBNull(8) ? null : reader.GetString(8),
            SlotIndex = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            ConditionPercent = reader.IsDBNull(10) ? null : reader.GetFloat(10),
        };

    public static ReportSummary CreateSummary(string id, DateTimeOffset receivedAt, InventoryReport report)
    {
        var playerItems = report.PlayerInventory.SelectMany(b => b.Items).ToList();
        var retainerItems = report.Retainers.SelectMany(r => r.Bags).SelectMany(b => b.Items).ToList();

        return new ReportSummary
        {
            Id = id,
            ReceivedAt = receivedAt,
            CharacterName = report.CharacterName,
            HomeWorld = report.HomeWorld,
            ReportTimestamp = report.Timestamp,
            PlayerBagCount = report.PlayerInventory.Count,
            PlayerItemStacks = playerItems.Count,
            PlayerItemQuantity = checked((int)playerItems.Sum(i => (long)i.Quantity)),
            RetainerCount = report.Retainers.Count,
            RetainerItemStacks = retainerItems.Count,
            RetainerItemQuantity = checked((int)retainerItems.Sum(i => (long)i.Quantity)),
        };
    }

    public static ReportSummary ReadSummary(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            ReceivedAt = ParseDateTimeOffset(reader.GetString(1)),
            CharacterName = reader.IsDBNull(2) ? null : reader.GetString(2),
            HomeWorld = reader.IsDBNull(3) ? null : reader.GetString(3),
            ReportTimestamp = reader.GetString(4),
            PlayerBagCount = checked((int)reader.GetInt64(5)),
            PlayerItemStacks = checked((int)reader.GetInt64(6)),
            PlayerItemQuantity = checked((int)reader.GetInt64(7)),
            RetainerCount = checked((int)reader.GetInt64(8)),
            RetainerItemStacks = checked((int)reader.GetInt64(9)),
            RetainerItemQuantity = checked((int)reader.GetInt64(10)),
        };

    public static InventoryRetentionSummary ReadRetentionSummary(SqliteDataReader reader) =>
        new()
        {
            SnapshotCount = checked((int)reader.GetInt64(0)),
            RawJsonRetainedCount = checked((int)reader.GetInt64(1)),
            RawJsonPrunedCount = checked((int)reader.GetInt64(2)),
            NewestSnapshotReceivedAtUtc = reader.IsDBNull(3)
                ? null
                : ParseDateTimeOffset(reader.GetString(3)),
            OldestSnapshotReceivedAtUtc = reader.IsDBNull(4)
                ? null
                : ParseDateTimeOffset(reader.GetString(4)),
        };

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed record InventorySnapshotRow(
    DateTimeOffset ReceivedAt,
    string? ApiKeyLabel,
    string? CharacterName,
    string? HomeWorld,
    string? ServiceAccountKey,
    ulong? PlayerGil,
    string ReportTimestamp,
    InventoryReportMetadata Metadata);

internal sealed record InventoryOwnerRow(
    long Id,
    string OwnerType,
    string OwnerName,
    ulong? RetainerId,
    string? LastUpdated,
    ulong? Gil);
