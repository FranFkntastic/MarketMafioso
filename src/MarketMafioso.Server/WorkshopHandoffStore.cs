using System.Text.Json;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Contracts.Workshop;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server;

public sealed class WorkshopHandoffException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class WorkshopHandoffStore(SqliteConnectionFactory connections, ILogger<WorkshopHandoffStore> log)
{
    public async Task<WorkshopHandoffList> ListAsync(long account, WorkshopCharacter actor, CancellationToken token)
    {
        actor = Validate(actor);
        await using var connection = await connections.OpenConnectionAsync(token);
        var known = await KnownAsync(connection, null, account, token);
        var all = await ReadAsync(connection, null, account, token);
        return new(known, all.Where(x => Same(x.Sender, actor)).OrderByDescending(x => x.UpdatedAt).ToArray(),
            all.Where(x => Same(x.Target, actor) && x.State == "Offered").OrderByDescending(x => x.UpdatedAt).ToArray(),
            all.Where(x => Same(x.Target, actor) && x.State == "Accepted").OrderByDescending(x => x.UpdatedAt).ToArray());
    }

    public async Task<WorkshopHandoffDto> OfferAsync(long account, WorkshopHandoffOffer request, CancellationToken token)
    {
        var sender = Validate(request.Sender);
        var target = Validate(request.Target);
        if (request.SourceJobId == Guid.Empty) throw new ArgumentException("Source job id is required.");
        var name = Text(request.JobName, "Job name", 200);
        if (request.Items is null || request.Items.Count is < 1 or > 200 ||
            request.Items.Any(x => x is null || x.WorkshopItemId == 0 || x.Quantity is < 1 or > 9999) ||
            request.Items.Select(x => x.WorkshopItemId).Distinct().Count() != request.Items.Count)
            throw new ArgumentException("Provide 1..200 distinct projects, each with project id > 0 and quantity 1..9999.");
        await using var connection = await connections.OpenConnectionAsync(token);
        using var write = connections.BeginWrite(connection);
        var transaction = write.Transaction;
        var known = await KnownAsync(connection, transaction, account, token);
        if (!known.Any(x => Same(x, target))) throw new WorkshopHandoffException(400, $"Unknown target character {target.CharacterName}@{target.World}; no inventory identity reported to this host login.");
        var all = await ReadAsync(connection, transaction, account, token);
        var previous = all.SingleOrDefault(x => x.SourceJobId == request.SourceJobId && Same(x.Target, target));
        if (previous is not null)
        {
            if (!Same(previous.Sender, sender)) throw Refuse(account, previous, 403, "Only the original sender may re-offer this share.");
            if (previous.State == "Accepted") throw Refuse(account, previous, 409, Describe(previous));
        }
        var value = new WorkshopHandoffDto(previous?.HandoffId ?? Guid.NewGuid(), request.SourceJobId, name,
            sender, target, request.Items.ToArray(), "Offered", NextTimestamp(previous),
            previous?.HandedOver, previous?.HandoverTotal ?? 0, previous?.HandoverUpdatedAt);
        await SaveAsync(connection, transaction, account, value, token);
        transaction.Commit();
        Transition(account, value);
        return value;
    }

    public async Task<WorkshopHandoffDto> ChangeAsync(long account, Guid id, string state, WorkshopHandoffCommand request, CancellationToken token)
    {
        var actor = Validate(request.Actor);
        await using var connection = await connections.OpenConnectionAsync(token);
        using var write = connections.BeginWrite(connection);
        var transaction = write.Transaction;
        var value = (await ReadAsync(connection, transaction, account, token)).SingleOrDefault(x => x.HandoffId == id)
            ?? throw new WorkshopHandoffException(404, $"Unknown handoff {id} for this host login.");
        if (!Same(state == "Withdrawn" ? value.Sender : value.Target, actor))
            throw Refuse(account, value, 403, $"Only the {(state == "Withdrawn" ? "sender" : "target")} may set this handoff to {state}.");
        if (value.State != "Offered") throw Refuse(account, value, 409, Describe(value));
        if (request.ExpectedUpdatedAt is { } expected && expected != value.UpdatedAt)
            throw Refuse(account, value, 409, $"Stale handoff {id}: offer superseded at {value.UpdatedAt:O}; review the updated share.");
        value = value with { State = state, UpdatedAt = NextTimestamp(value) };
        await SaveAsync(connection, transaction, account, value, token);
        transaction.Commit();
        Transition(account, value);
        return value;
    }

    private void Transition(long account, WorkshopHandoffDto value) => log.LogInformation(
        "Workshop handoff {HandoffId} account {Account} sender {Sender} target {Target}: {State}",
        value.HandoffId, account, value.Sender, value.Target, value.State);

    public async Task<WorkshopHandoffDto> ReportHandoverAsync(long account, Guid id, WorkshopHandoverReport request, CancellationToken token)
    {
        var actor = Validate(request.Actor);
        await using var connection = await connections.OpenConnectionAsync(token);
        using var write = connections.BeginWrite(connection);
        var transaction = write.Transaction;
        var value = (await ReadAsync(connection, transaction, account, token)).SingleOrDefault(x => x.HandoffId == id)
            ?? throw new WorkshopHandoffException(404, $"Unknown handoff {id} for this host login.");
        if (!Same(value.Sender, actor))
            throw Refuse(account, value, 403, "Only the sender may report material handover for this share.");
        if (value.State is not ("Offered" or "Accepted"))
            throw Refuse(account, value, 409, $"Handoff {id} is {value.State}; material handover requires Offered or Accepted state.");
        if (request.HandoverTotal is < 0 or > 10_000_000 || request.HandedOver is null || request.HandedOver.Count > 500 ||
            request.HandedOver.Any(x => x is null || x.ItemId == 0 || x.Quantity is < 0 or > 1_000_000) ||
            request.HandedOver.Select(x => x.ItemId).Distinct().Count() != request.HandedOver.Count)
            throw Refuse(account, value, 400, "Handover requires at most 500 distinct item ids > 0, quantities 0..1,000,000, and HandoverTotal 0..10,000,000.");
        var items = request.HandedOver.OrderBy(x => x.ItemId).ToArray();
        // A repeated SET is a no-op, including its timestamp. Never advance the offer version.
        if (value.HandoverUpdatedAt is null || value.HandoverTotal != request.HandoverTotal ||
            !(value.HandedOver ?? []).OrderBy(x => x.ItemId).SequenceEqual(items))
        {
            value = value with { HandedOver = items, HandoverTotal = request.HandoverTotal,
                HandoverUpdatedAt = value.HandoverUpdatedAt is { } previous && previous >= DateTimeOffset.UtcNow
                    ? previous.AddTicks(1) : DateTimeOffset.UtcNow };
            await SaveAsync(connection, transaction, account, value, token);
        }
        transaction.Commit();
        log.LogInformation("Workshop handoff {HandoffId} account {Account} sender {Sender} target {Target}: {State} handover SET {ItemCount} items, total {HandoverTotal}",
            value.HandoffId, account, value.Sender, value.Target, value.State, items.Length, value.HandoverTotal);
        return value;
    }

    public async Task<WorkshopCharacterHoldings> GetHoldingsAsync(long account, WorkshopCharacter character,
        string? itemIds, InventoryReportStore reports, CancellationToken token)
    {
        character = Validate(character);
        var parts = itemIds?.Split(',') ?? [];
        var ids = new HashSet<uint>();
        if (parts.Length is < 1 or > 200 || parts.Any(part =>
                !uint.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ||
                id == 0 || !ids.Add(id)))
            throw new ArgumentException("itemIds must contain 1..200 distinct positive integer item ids separated by commas.");
        long characterId;
        await using (var connection = await connections.OpenConnectionAsync(token))
        {
            var known = await KnownAsync(connection, null, account, token);
            character = known.FirstOrDefault(x => Same(x, character))
                ?? throw new WorkshopHandoffException(404, $"Unknown character {character.CharacterName}@{character.World}; no inventory identity reported to this host login.");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM characters WHERE account_id = $account AND character_name = $name COLLATE NOCASE AND home_world = $world COLLATE NOCASE ORDER BY last_seen_at_utc DESC LIMIT 1";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$name", character.CharacterName);
            command.Parameters.AddWithValue("$world", character.World);
            characterId = (long)(await command.ExecuteScalarAsync(token)
                ?? throw new WorkshopHandoffException(404, "Character inventory identity is no longer available."));
        }
        var latest = await reports.GetLatestAsync(account, characterId, token);
        var totals = ids.ToDictionary(id => id, _ => 0);
        var retainers = ids.ToDictionary(id => id, _ => 0);
        DateTimeOffset? capturedAt = null;
        if (latest is not null)
        {
            if (!DateTimeOffset.TryParse(latest.Report.Timestamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var captured))
                throw new WorkshopHandoffException(503, "Latest inventory report has an invalid capture timestamp.");
            capturedAt = captured;
            var retainerBags = latest.Report.Retainers
                .Where(r => (string.IsNullOrWhiteSpace(r.OwnerCharacterName) || string.Equals(r.OwnerCharacterName, character.CharacterName, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(r.OwnerHomeWorld) || string.Equals(r.OwnerHomeWorld, character.World, StringComparison.OrdinalIgnoreCase)))
                // Only bags observed in this report: the reporter carries forward unobserved retainer bags from
                // older reports, and counting those as current would under-send materials.
                .SelectMany(r => r.Bags.Where(b => r.Storage.ObservedSources.Contains(b.Location ?? b.BagName, StringComparer.Ordinal)));
            AddBags(latest.Report.PlayerInventory, totals);
            AddBags(retainerBags, retainers);
        }
        return new(character, capturedAt,
            ids.Order().Select(id => new WorkshopMaterialQuantity(id, totals[id])).ToArray(),
            ids.Order().Select(id => new WorkshopMaterialQuantity(id, retainers[id])).ToArray());

        void AddBags(IEnumerable<InventoryBag> bags, Dictionary<uint, int> quantities)
        {
            foreach (var item in bags.SelectMany(b => b.Items))
                if (quantities.ContainsKey(item.ItemId))
                {
                    var total = (long)quantities[item.ItemId] + item.Quantity;
                    if (total > int.MaxValue) throw new WorkshopHandoffException(503, $"Inventory quantity for item {item.ItemId} exceeds the supported holdings range.");
                    quantities[item.ItemId] = (int)total;
                }
        }
    }

    private WorkshopHandoffException Refuse(long account, WorkshopHandoffDto value, int status, string reason)
    {
        log.LogWarning("Workshop handoff {HandoffId} account {Account} sender {Sender} target {Target} refused: {Reason}",
            value.HandoffId, account, value.Sender, value.Target, reason);
        return new(status, reason);
    }

    private static string Describe(WorkshopHandoffDto value) =>
        $"Handoff {value.HandoffId} already {value.State.ToLowerInvariant()} by {(value.State == "Withdrawn" ? value.Sender : value.Target)} at {value.UpdatedAt:O}; only Offered handoffs can transition.";

    private static DateTimeOffset NextTimestamp(WorkshopHandoffDto? previous) =>
        previous is not null && previous.UpdatedAt >= DateTimeOffset.UtcNow ? previous.UpdatedAt.AddTicks(1) : DateTimeOffset.UtcNow;

    internal static WorkshopCharacter Validate(WorkshopCharacter? value) => value is null
        ? throw new ArgumentException("Character identity is required.")
        : new(Text(value.CharacterName, "Character name", 64), Text(value.World, "Home world", 64));

    private static string Text(string? text, string label, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > max || text.Any(char.IsControl))
            throw new ArgumentException($"{label} must contain 1..{max} characters and no control characters.");
        return text.Trim();
    }

    private static bool Same(WorkshopCharacter a, WorkshopCharacter b) =>
        string.Equals(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);

    private static async Task<WorkshopCharacter[]> KnownAsync(SqliteConnection connection, SqliteTransaction? transaction, long account, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT character_name, home_world FROM characters WHERE account_id = $account AND trim(character_name) <> '' AND trim(coalesce(home_world, '')) <> '' ORDER BY character_name, home_world";
        command.Parameters.AddWithValue("$account", account);
        using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<WorkshopCharacter>();
        while (await reader.ReadAsync(token)) values.Add(new(reader.GetString(0), reader.GetString(1)));
        return values.ToArray();
    }

    private static async Task<List<WorkshopHandoffDto>> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction, long account, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM workshop_handoffs WHERE account_id = $account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<WorkshopHandoffDto>();
        while (await reader.ReadAsync(token)) values.Add(JsonSerializer.Deserialize<WorkshopHandoffDto>(reader.GetString(0))!);
        return values;
    }

    private static async Task SaveAsync(SqliteConnection connection, SqliteTransaction transaction, long account, WorkshopHandoffDto value, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workshop_handoffs(id, account_id, source_job_id, target_name, target_world, payload_json)
            VALUES($id, $account, $source, $target, $world, $json)
            ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json WHERE account_id = $account;
            """;
        command.Parameters.AddWithValue("$id", value.HandoffId.ToString());
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$source", value.SourceJobId.ToString());
        command.Parameters.AddWithValue("$target", value.Target.CharacterName);
        command.Parameters.AddWithValue("$world", value.Target.World);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
        await command.ExecuteNonQueryAsync(token);
    }
}
