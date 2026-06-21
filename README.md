# MarketMafioso

MarketMafioso is a Dalamud plugin workspace for retainer inventory, active sale listings, and market-board workflow experiments.

This repository is being cleaned up from two older local projects:

- `MarketMafioso`, an old dev plugin for active retainer sale listing capture and market lookup.
- `InventoryReporter`, a smaller plugin that exports player and retainer inventory snapshots over HTTP.

The current direction is to merge the useful inventory/reporting pieces into MarketMafioso while reworking the older MarketMafioso UI and capture flow instead of preserving it wholesale.

## Layout

```text
MarketMafioso/            Active Dalamud plugin project
external/InventoryReporter/  Imported reference source for the merge
```

`external/InventoryReporter` is kept as reference material. It is not part of the active solution and should not be treated as a second plugin to ship from this repository.

## Development

Build the active plugin:

```powershell
dotnet build MarketMafioso.sln -c Release -p:UseSharedCompilation=false
```

For local dev-plugin iteration, Debug builds still run `MarketMafioso/tools/Sync-DevPlugin.ps1` and copy output to:

```text
%APPDATA%\XIVLauncher\devPlugins\MarketMafioso
```

## Cleanup Notes

The old local dev root included generated build folders, Visual Studio state, and large third-party reference checkouts. Those were intentionally left out of this repository. If old reference code is needed during a rewrite, keep it outside the repo or add a small source note instead of vendoring the full checkout.
