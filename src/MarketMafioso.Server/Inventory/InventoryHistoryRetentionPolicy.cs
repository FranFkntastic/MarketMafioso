using System.Globalization;

namespace MarketMafioso.Server.Inventory;

internal enum InventoryHistoryRetentionMode
{
    Tiered,
    Count,
}

internal sealed record InventoryHistoryCandidate(
    string Id,
    long? CharacterId,
    DateTimeOffset ReceivedAt,
    bool HasRetainers);

/// <summary>
/// Decides which non-current inventory history snapshots may be pruned.
/// Tiered mode keeps every semantic change for <see cref="KeepAllDays"/>, then the last snapshot of each
/// UTC hour until <see cref="HourlyDays"/>, then the last snapshot of each UTC day until
/// <see cref="DailyDays"/>, and the last snapshot of each ISO week forever. Within a bucket, a snapshot
/// that carries retainer evidence is preferred over a player-only (partial) one.
/// The newest <see cref="MinimumRecent"/> history entries per character are always kept.
/// Count mode is the legacy behavior: keep only the newest <see cref="MinimumRecent"/> entries.
/// </summary>
internal sealed record InventoryHistoryRetentionPolicy(
    InventoryHistoryRetentionMode Mode,
    int MinimumRecent,
    int KeepAllDays,
    int HourlyDays,
    int DailyDays,
    bool CarryForwardMissingRetainers)
{
    public const int DefaultKeepAllDays = 14;
    public const int DefaultHourlyDays = 90;
    public const int DefaultDailyDays = 365;

    public static InventoryHistoryRetentionPolicy Resolve(IConfiguration configuration)
    {
        var modeText = configuration.GetValue<string?>("MarketMafioso:InventoryHistory:Policy");
        var mode = InventoryHistoryRetentionMode.Tiered;
        if (!string.IsNullOrWhiteSpace(modeText) &&
            !Enum.TryParse(modeText.Trim(), ignoreCase: true, out mode))
        {
            throw new InvalidOperationException(
                "MarketMafioso:InventoryHistory:Policy must be 'Tiered' or 'Count'.");
        }

        var minimumRecent = InventoryReportWritePersistence.ResolveHistoryRetention(configuration);
        var keepAllDays = configuration.GetValue("MarketMafioso:InventoryHistory:KeepAllDays", DefaultKeepAllDays);
        var hourlyDays = configuration.GetValue("MarketMafioso:InventoryHistory:HourlyDays", DefaultHourlyDays);
        var dailyDays = configuration.GetValue("MarketMafioso:InventoryHistory:DailyDays", DefaultDailyDays);
        var carryForward = configuration.GetValue("MarketMafioso:InventoryHistory:CarryForwardMissingRetainers", true);

        if (mode == InventoryHistoryRetentionMode.Count && minimumRecent < 1)
            throw new InvalidOperationException("MarketMafioso:InventoryHistoryRetentionPerCharacter must be one or greater.");
        if (mode == InventoryHistoryRetentionMode.Tiered)
        {
            if (minimumRecent < 0)
                throw new InvalidOperationException("MarketMafioso:InventoryHistoryRetentionPerCharacter must be zero or greater.");
            if (keepAllDays < 0 || hourlyDays < keepAllDays || dailyDays < hourlyDays)
            {
                throw new InvalidOperationException(
                    "MarketMafioso:InventoryHistory tiers must satisfy 0 <= KeepAllDays <= HourlyDays <= DailyDays.");
            }
        }

        return new InventoryHistoryRetentionPolicy(mode, minimumRecent, keepAllDays, hourlyDays, dailyDays, carryForward);
    }

    public string Describe() => Mode == InventoryHistoryRetentionMode.Count
        ? $"Newest {MinimumRecent:N0} changes per character"
        : $"All changes for {KeepAllDays:N0} days, hourly to {HourlyDays:N0} days, daily to {DailyDays:N0} days, weekly forever" +
          (MinimumRecent > 0 ? $" (newest {MinimumRecent:N0} always kept)" : string.Empty);

    /// <summary>Returns the ids of tiered-mode candidates that should be pruned.</summary>
    public IReadOnlyList<string> SelectPrunable(IEnumerable<InventoryHistoryCandidate> candidates, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var prunable = new List<string>();
        foreach (var character in candidates.GroupBy(candidate => candidate.CharacterId))
        {
            var buckets = character
                .OrderByDescending(candidate => candidate.ReceivedAt)
                .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
                .Skip(MinimumRecent)
                .Select(candidate => (Candidate: candidate, Bucket: BucketKey(candidate.ReceivedAt, now)))
                .Where(entry => entry.Bucket is not null)
                .GroupBy(entry => entry.Bucket!, StringComparer.Ordinal);

            foreach (var bucket in buckets)
            {
                prunable.AddRange(bucket
                    .Select(entry => entry.Candidate)
                    .OrderByDescending(candidate => candidate.HasRetainers)
                    .ThenByDescending(candidate => candidate.ReceivedAt)
                    .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
                    .Skip(1)
                    .Select(candidate => candidate.Id));
            }
        }

        return prunable;
    }

    private string? BucketKey(DateTimeOffset receivedAt, DateTimeOffset now)
    {
        var age = now - receivedAt;
        var utc = receivedAt.UtcDateTime;
        if (age < TimeSpan.FromDays(KeepAllDays))
            return null;
        if (age < TimeSpan.FromDays(HourlyDays))
            return "h:" + utc.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);
        if (age < TimeSpan.FromDays(DailyDays))
            return "d:" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"w:{ISOWeek.GetYear(utc)}-{ISOWeek.GetWeekOfYear(utc):D2}");
    }
}
