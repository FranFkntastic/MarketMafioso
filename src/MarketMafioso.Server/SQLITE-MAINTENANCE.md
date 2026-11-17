# Workshop Host SQLite contention and maintenance

## Retention and catch-up

An explicitly configured `MarketMafioso:SnapshotRetentionCount` now selects Count
retention unless `MarketMafioso:InventoryHistory:Policy` explicitly selects another
policy. Count retains that many **non-current** snapshots per character, plus its
current head. `InventoryHistoryRetentionPerCharacter`, if supplied, takes precedence
over the legacy count value. Explicit Tiered remains intentional long-term history:
all changes for 14 days by default, hourly to 90 days, daily to 365 days, weekly
forever; its count is a minimum, not a maximum. Previously Tiered was selected even
when only SnapshotRetentionCount was configured, so 500 never imposed a cap.

The hosted retention worker starts after ten seconds and rescans persisted state
after each ten-second rest. It does not depend on new uploads. For each account it
selects candidates without a write transaction, deletes at most 100 snapshots per
pass in separate IMMEDIATE transactions, and waits 250 ms between snapshots.
Each snapshot remains atomic: its indexed owner/bag/item/listing cascades complete
together. It rechecks account, non-current status and minimum recent history under
the write lock. One unusually large snapshot can still take longer than a second;
transaction-scope warnings identify this, rather than promising a hard time bound.
Maintenance lock acquisition times out after one second and retries on a later pass.

Raw JSON is cleared in batches of ten per account per pass, independently of semantic
changes. Superseded Published/Failed projection rows are removed in batches of 100,
up to 20 batches per pass with 250 ms yields; ten empty obsolete generations are
then removed. Current and Building generations are protected. Interrupted staging
is marked Failed at the next projection attempt or startup. Run one host process
per database; startup assumes there is no other host actively building projections.

Inventory owners, bags, items and retainer_market_listings are snapshot children,
so Count retention bounds their snapshot multiplicity without deleting current
holdings. Their absolute row counts also depend on inventory size. Market evidence,
payloads, actor/name/listing evidence, owned-listing versions, sale events, acquisition
records and handoff receipts are intentional audit/history, not disposable inventory
copies; this change does not silently delete them. World/region diagnostic observations
use their existing time windows, now deleting at most 100 rows of each per collection
pass. Diagnostic events retain their existing count policy with 100-row catch-up batches.

Projection computation and JSON serialization happen before writing. Generations
stage in 50-row transactions with 25 ms yields, then publish atomically; only outbox
entries present before the evidence read are acknowledged. A per-store gate serializes
builders so an older computation cannot publish over a newer one. Foreground snapshot
insertion, user-requested snapshot deletion and account-wide deletion remain atomic
operations, not background cleanup. No large index build or table rewrite is added.

## Operator deployment (not performed by this change)

1. Take and verify a SQLite-aware backup to separate storage. Preserve it before
   enabling catch-up: deleting history is not reversible by rolling back the binary.
   Review whether Count or Tiered matches the desired history; explicitly set
   `MarketMafioso__InventoryHistory__Policy=Count`,
   `MarketMafioso__SnapshotRetentionCount=500` and
   `MarketMafioso__RawJsonRetentionCount=20` for the count-bounded policy. Remove or
   reconcile an overriding `InventoryHistoryRetentionPerCharacter` setting.
2. Publish the reviewed server commit using the normal server release pipeline.
   Stop the existing service, install that artifact, and start exactly one instance.
   Do not launch a second instance against the same database during a rolling upgrade.
3. Check health, authenticated inventory reads, inventory upload, and handoff refresh
   and handover using authorized fixtures. Check the journal for `SQLite busy waiting
   for write operation`, `SQLite busy in`, `transaction scope lasted`, retention progress,
   and skipped credential last-used bookkeeping. Last-used failure no longer rejects
   a successfully validated key; actual key validation still fails closed.
4. Track snapshot counts with the read-only query below. Allow catch-up to finish
   over hours; do not raise batch sizes or issue a bulk cascading DELETE to accelerate
   it. Check `PRAGMA page_count; PRAGMA freelist_count; PRAGMA page_size;` periodically.
   A growing freelist with unchanged file size means pages are available for reuse,
   not that retention has failed. Confirm current inventories and latest projection
   remain readable throughout catch-up. Stop/restart is safe; selection is recomputed.

```sql
SELECT account_id, character_id,
       SUM(is_current = 1) AS current_heads,
       SUM(is_current = 0) AS history
FROM snapshots GROUP BY account_id, character_id;
PRAGMA auto_vacuum;
PRAGMA page_count;
PRAGMA freelist_count;
PRAGMA page_size;
```

## Disk reclamation after catch-up

1. Keep the verified backup off the host's constrained filesystem. Schedule downtime,
   stop the host and all other database clients, then use sqlite3 against the exact
   configured database. Run `PRAGMA wal_checkpoint(TRUNCATE);` and verify its first
   result is zero (not busy). Never manually discard a live WAL file.
2. Inspect `PRAGMA auto_vacuum;`. If it is already 2 (INCREMENTAL), issue
   `PRAGMA incremental_vacuum(1000);` repeatedly in separate invocations, checking
   freelist and free space between calls. Checkpoint again afterward. If it is 0,
   incremental_vacuum cannot reclaim space: changing NONE to INCREMENTAL requires
   a rebuilding VACUUM, not a cheap pragma-only migration.
3. For this 10.16 GB database, ordinary VACUUM can require **up to 20.32 GB of extra
   free space**, plus operational/WAL margin. The reported 15 GB free is not a safe
   budget even after pruning (the original file is still 10.16 GB). Expand storage
   before running it. With that space and the service stopped, run `VACUUM;`, then
   `PRAGMA wal_checkpoint(TRUNCATE);`. If future incremental reclamation is desired,
   set `PRAGMA auto_vacuum=INCREMENTAL;` immediately before that maintenance VACUUM.
4. Alternatively, with the service stopped, use `VACUUM INTO '/absolute/new-file.db';`
   on adequately provisioned storage. This produces a compact copy without the
   copy-back WAL cost; budget at least the current DB size plus margin for output
   rather than assuming a post-prune size. Run `PRAGMA integrity_check;` and
   `PRAGMA foreign_key_check;` on the copy and compare current-head counts. Replace
   the stopped, checkpointed original using the established backup/restore procedure,
   preserving the original off-host and restoring file ownership and permissions.
   Never point a running host at the copy or mix old WAL/SHM files with it.
5. Restart the host, check health and the authenticated inventory/handoff journeys
   again, and verify free space and current inventories before retiring any backup.

SQLite references: https://www.sqlite.org/lang_vacuum.html and
https://www.sqlite.org/pragma.html#pragma_auto_vacuum.
