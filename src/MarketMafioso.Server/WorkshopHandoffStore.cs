using System.Text.Json;
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
            all.Where(x => Same(x.Target, actor) && x.State == "Offered").OrderByDescending(x => x.UpdatedAt).ToArray());
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
        using var transaction = connection.BeginTransaction();
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
            sender, target, request.Items.ToArray(), "Offered", NextTimestamp(previous));
        await SaveAsync(connection, transaction, account, value, token);
        transaction.Commit();
        Transition(account, value);
        return value;
    }

    public async Task<WorkshopHandoffDto> ChangeAsync(long account, Guid id, string state, WorkshopHandoffCommand request, CancellationToken token)
    {
        var actor = Validate(request.Actor);
        await using var connection = await connections.OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction();
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
