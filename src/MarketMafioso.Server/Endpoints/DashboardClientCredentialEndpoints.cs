using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Endpoints;

internal static class DashboardClientCredentialEndpoints
{
    public static void MapDashboardClientCredentialEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/client-keys", ListClientCredentials);
        app.MapPost("/api/settings/client-keys", CreateClientCredential);
        app.MapDelete("/api/settings/client-keys/{id:long}", RevokeClientCredential);
    }

    private static async Task<IResult> ListClientCredentials(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        WorkshopHostCredentialStore credentialStore,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        return Results.Ok(await credentialStore.ListAsync(accountIds, token));
    }

    private static async Task<IResult> CreateClientCredential(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        WorkshopHostCredentialStore credentialStore,
        ClientCredentialCreateRequest request,
        CancellationToken token)
    {
        if (!WorkshopHostCredentialPurposes.IsSupported(request.Purpose))
            return Results.BadRequest(new { error = "unsupported_client_key_purpose" });

        var label = request.Label.Trim();
        if (label.Length is < 1 or > 80)
            return Results.BadRequest(new { error = "invalid_client_key_label" });

        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var created = await credentialStore.CreateAsync(accountIds[0], label, request.Purpose, token);
        return Results.Created($"{context.Request.PathBase}/api/settings/client-keys/{created.Id}", created);
    }

    private static async Task<IResult> RevokeClientCredential(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        WorkshopHostCredentialStore credentialStore,
        long id,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        return await credentialStore.RevokeAsync(id, accountIds, token)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IReadOnlyList<long>> GetAccountIdsAsync(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        if (!context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) || value is not long userId)
            return [1];

        var accountIds = new List<long>();
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id
            FROM dashboard_user_accounts
            WHERE dashboard_user_id = $dashboardUserId
            ORDER BY is_default DESC, account_id
            """;
        command.Parameters.AddWithValue("$dashboardUserId", userId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            accountIds.Add(reader.GetInt64(0));

        return accountIds.Count == 0 ? [1] : accountIds;
    }
}
