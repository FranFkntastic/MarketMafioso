namespace MarketMafioso.Server.Persistence;

internal static class WorkshopHandoffSchema
{
    // Additive, idempotent migration, applied in the host schema transaction.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS workshop_handoffs (
            id TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            source_job_id TEXT NOT NULL,
            target_name TEXT NOT NULL COLLATE NOCASE,
            target_world TEXT NOT NULL COLLATE NOCASE,
            payload_json TEXT NOT NULL,
            UNIQUE(account_id, source_job_id, target_name, target_world)
        );
        CREATE INDEX IF NOT EXISTS ix_workshop_handoffs_account ON workshop_handoffs(account_id);
        """;
}
