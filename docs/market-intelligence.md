# Market intelligence

Workshop Host can retain immutable market observations from Market Acquisition routes, ordinary market-board browsing, historical route exports, and future aggregate data providers. The dashboard's **Intelligence** page groups that evidence by item and world; each row exposes its measurements, source coverage, changes from the preceding book, derived findings, raw observation history, and a directly editable review note.

The evidence model keeps `Complete`, `Partial`, `LegacyMissing`, `Empty`, `Unavailable`, and `AggregateOnly` observations distinct. `LegacyMissing` means listing rows survived but the older artifact did not record enough coverage metadata to call the book complete. Listing payloads are content-addressed, while observation occurrences remain independently idempotent, so retrying one delivery cannot duplicate it and seeing the same book later does not erase the later occurrence.

Evidence schema V2 can also report the seller owner's and item's maker identity when the capture source actually observed them. Workshop Host immediately replaces those game identifiers with account-scoped HMAC actor keys backed by a durable random account secret; raw content IDs are neither stored in evidence payloads nor returned by market-intelligence APIs. Actor identity remains explicitly `Observed`, `Absent`, or `NotCaptured`, which prevents missing identity from silently becoming evidence of a different actor. Retainer names remain descriptive observations, while seller IDs retain their original world scope.

Derived rows are versioned rebuildable projections. Actor-aware findings measure owner concentration, producer concentration, multi-retainer ownership, self-crafted supply, recurring producers, and producer replacement only when their declared coverage thresholds are satisfied. A new generation becomes current only after every row is built successfully, and the durable projection outbox survives process failure. Server-sent events are refresh hints only: reconnecting clients always read the current account-scoped projection from the API.

## Historical import

`src/MarketMafioso/tools/Import-MarketIntelligenceCorpus.py` streams package-local CSV and legacy gzip CSV inputs without extracting them. A dry run validates the reconstructed corpus and classifier counts:

```powershell
python .\src\MarketMafioso\tools\Import-MarketIntelligenceCorpus.py `
  --root "<market-acquisition-route-logs>" `
  --dry-run
```

To upload, provide the Workshop Host base URL and a file containing an ingest key. Evidence is durably accepted with projection deferred, per-artifact receipts are recorded using hashes instead of local paths, and one atomic rebuild publishes the completed corpus:

```powershell
python .\src\MarketMafioso\tools\Import-MarketIntelligenceCorpus.py `
  --root "<market-acquisition-route-logs>" `
  --endpoint "https://host.example/marketmafioso" `
  --api-key-file "<private-key-file>"
```

The importer is resumable: deterministic observation keys make repeated deliveries no-ops, while changed artifacts receive new fingerprints and receipts.
