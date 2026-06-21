# MarketMafioso

MarketMafioso is a Dalamud plugin workspace for inventory reporting first, with market workflow tools planned on top of that baseline.

This repository is being cleaned up from two older local projects:

- `MarketMafioso`, an old dev plugin with exploratory market tooling.
- `InventoryReporter`, a smaller plugin that exports player and retainer inventory snapshots over HTTP.

The active plugin is intentionally InventoryReporter in almost all but name: it scans player inventory, caches retainer inventories when retainer windows close, and can send JSON snapshots to a configurable HTTP endpoint. The older MarketMafioso market-capture experiment is not part of the active plugin baseline.

## Layout

```text
MarketMafioso/            Active Dalamud plugin project
```

## Development

Build the active plugin:

```powershell
dotnet build MarketMafioso.sln -c Release -p:UseSharedCompilation=false
```

Open the settings window in game with:

```text
/mmf
```

Send a report immediately with:

```text
/mmf send
```

For local dev-plugin iteration, Debug builds still run `MarketMafioso/tools/Sync-DevPlugin.ps1` and copy output to:

```text
%APPDATA%\XIVLauncher\devPlugins\MarketMafioso
```

## Cleanup Notes

The old local dev root included generated build folders, Visual Studio state, and large third-party reference checkouts. Those were intentionally left out of this repository. If old reference code is needed during a rewrite, keep it outside the repo or add a small source note instead of vendoring the full checkout.
