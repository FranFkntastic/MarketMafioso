using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.WorkshopHost;

public sealed class CraftAppraisalPlanStore
{
    private readonly string planDirectory;

    public CraftAppraisalPlanStore(SqliteConnectionFactory connectionFactory)
    {
        var databaseDirectory = Path.GetDirectoryName(connectionFactory.DatabasePath)
            ?? throw new InvalidOperationException("The Workshop Host database path has no parent directory.");
        planDirectory = Path.Combine(databaseDirectory, "craft-appraisal-plans");
    }

    public async Task<string> SaveAsync(string planJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planJson);
        Directory.CreateDirectory(planDirectory);
        var planId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(PlanPath(planId), planJson, cancellationToken);
        return planId;
    }

    public async Task<string?> ReadAsync(string planId, CancellationToken cancellationToken)
    {
        if (planId.Length != 32 || !planId.All(Uri.IsHexDigit))
            return null;

        var path = PlanPath(planId);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
    }

    private string PlanPath(string planId) => Path.Combine(planDirectory, $"{planId}.craftplan");
}
