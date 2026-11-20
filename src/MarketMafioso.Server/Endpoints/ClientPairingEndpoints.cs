using System.Globalization;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Endpoints;

internal static class ClientPairingEndpoints
{
    public const string RedeemPath = "/api/settings/client-pairing/redeem";
    private const int MaxClientNameLength = 40;

    public static void MapClientPairingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/settings/client-pairing", CreatePairingCode);
        app.MapPost(RedeemPath, RedeemPairingCode);
    }

    private static async Task<IResult> CreatePairingCode(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        ClientPairingCodeStore pairingCodes,
        ILoggerFactory loggerFactory,
        CancellationToken token)
    {
        var accountIds = await DashboardClientCredentialEndpoints.GetAccountIdsAsync(context, connectionFactory, token);
        var created = await pairingCodes.CreateAsync(accountIds[0], token);
        if (created is null)
        {
            loggerFactory.CreateLogger(typeof(ClientPairingEndpoints)).LogWarning(
                "Refused a pairing code for account {AccountId}: {Outstanding} unexpired codes are already outstanding.",
                accountIds[0],
                ClientPairingCodeStore.MaxOutstandingCodesPerAccount);
            return Results.Json(
                new ClientPairingErrorResponse(
                    "too_many_pending_codes",
                    $"{ClientPairingCodeStore.MaxOutstandingCodesPerAccount} unused codes are still active. Use one or wait for them to expire."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Ok(new ClientPairingCodeResponse
        {
            Token = created.Token,
            ExpiresAtUtc = created.ExpiresAtUtc,
        });
    }

    private static async Task<IResult> RedeemPairingCode(
        HttpContext context,
        ClientPairingRedeemRequest? request,
        ClientPairingCodeStore pairingCodes,
        WorkshopHostCredentialStore credentialStore,
        ILoggerFactory loggerFactory,
        CancellationToken token)
    {
        var log = loggerFactory.CreateLogger(typeof(ClientPairingEndpoints));
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var suppliedToken = request?.Token?.Trim() ?? string.Empty;
        if (suppliedToken.Length is < 16 or > 128)
        {
            log.LogWarning(
                "Pairing redeem from {Remote} rejected as invalid_code: token length {Length} is outside the accepted range.",
                remote,
                suppliedToken.Length);
            return Failure(StatusCodes.Status400BadRequest, "invalid_code", "This connection code is not valid. Copy a new one from the dashboard.");
        }

        var claim = await pairingCodes.ClaimAsync(suppliedToken, token);
        switch (claim.Outcome)
        {
            case ClientPairingRedeemOutcome.InvalidCode:
                log.LogWarning("Pairing redeem from {Remote} rejected as invalid_code: no pairing code matches.", remote);
                return Failure(StatusCodes.Status404NotFound, "invalid_code", "This connection code is not valid. Copy a new one from the dashboard.");
            case ClientPairingRedeemOutcome.Expired:
                log.LogWarning(
                    "Pairing redeem from {Remote} rejected as expired: code {PairingId} for account {AccountId} expired at {ExpiresAtUtc:O}.",
                    remote, claim.PairingId, claim.AccountId, claim.ExpiresAtUtc);
                return Failure(StatusCodes.Status410Gone, "expired", "This connection code has expired. Create a new one from the dashboard.");
            case ClientPairingRedeemOutcome.AlreadyUsed:
                log.LogWarning(
                    "Pairing redeem from {Remote} rejected as already_used: code {PairingId} for account {AccountId} was redeemed at {RedeemedAtUtc:O}.",
                    remote, claim.PairingId, claim.AccountId, claim.RedeemedAtUtc);
                return Failure(StatusCodes.Status409Conflict, "already_used", "This connection code was already used. Create a new one from the dashboard.");
        }

        var label = BuildLabel(request?.ClientName, claim.RedeemedAtUtc ?? DateTimeOffset.UtcNow);
        ClientCredentialCreatedView credential;
        try
        {
            credential = await credentialStore.CreateAsync(
                claim.AccountId,
                label,
                WorkshopHostCredentialPurposes.MarketMafiosoClient,
                token);
            await pairingCodes.AttachCredentialAsync(claim.PairingId, credential.Id, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LogError(
                exception,
                "Pairing code {PairingId} for account {AccountId} was claimed but issuing its client key failed; the code is spent.",
                claim.PairingId,
                claim.AccountId);
            return Failure(StatusCodes.Status500InternalServerError, "pairing_failed", "The server could not issue a client key. Create a new code and try again.");
        }

        log.LogInformation(
            "Pairing code {PairingId} for account {AccountId} redeemed from {Remote}; issued client key {CredentialId} ({KeyPrefix}...).",
            claim.PairingId, claim.AccountId, remote, credential.Id, credential.KeyPrefix);
        return Results.Ok(new ClientPairingRedeemResponse
        {
            ApiKey = credential.Secret,
            CredentialId = credential.Id,
            Label = credential.Label,
        });
    }

    private static string BuildLabel(string? clientName, DateTimeOffset pairedAt)
    {
        var date = pairedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = new string((clientName ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length > MaxClientNameLength)
            name = name[..MaxClientNameLength].TrimEnd();
        return name.Length == 0
            ? $"Game client (paired {date})"
            : $"Game client: {name} (paired {date})";
    }

    private static IResult Failure(int statusCode, string error, string message) =>
        Results.Json(new ClientPairingErrorResponse(error, message), statusCode: statusCode);
}

internal sealed record ClientPairingErrorResponse(string Error, string Message);
