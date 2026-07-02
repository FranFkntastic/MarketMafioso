# MarketMafioso

MarketMafioso is a Dalamud plugin for Workshop Logistics and optional self-hosted inventory history.

## Start Here

Most users only need two steps:

1. **Install the plugin:** copy the FranFkntastic plugin repository URL into Dalamud's Experimental > Custom Plugin Repositories settings, then install MarketMafioso from `/xlplugins`.

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

2. **Optional receiver dashboard:** download `marketmafioso-self-host-<version>.zip` from the GitHub [Releases](https://github.com/FranFkntastic/MarketMafioso/releases) page, extract it somewhere permanent, install Docker Desktop if needed, then double-click `Install Receiver.bat`.

Full step-by-step instructions live in [docs/installation.md](docs/installation.md).

## What You Get

### Works Without The Receiver

- **Workshop Logistics** builds Free Company workshop material queues, checks player and retainer stock, withdraws available materials from retainers, can execute the MarketMafioso queue natively, and can still send the prepared queue to VIWI Workshoppa.

### With The Optional Receiver

- The optional receiver stores inventory snapshots in SQLite and serves the browser dashboard.
- Retainer cache data makes those stored snapshots more useful after you have refreshed retainer data in game.

## Opening The Plugin

Open MarketMafioso in game:

```text
/mmf
```

To use inventory history, install the receiver first, then paste the receiver Server URL and Client API Key into MarketMafioso settings.

## Notes

Retainer inventory is only as fresh as the last cache refresh or retainer window scan. For the best Workshop Logistics results, refresh the retainer cache before planning a restock.

The receiver backend can be run privately or self-hosted from the server package. MarketMafioso does not provide public multi-user inventory hosting; users who want persistent hosted inventory storage should run their own receiver instance.

Installation notes for the plugin and optional receiver live in [docs/installation.md](docs/installation.md). Self-hosting notes live in [docs/self-hosting.md](docs/self-hosting.md).

## Acknowledgements

MarketMafioso is built in conversation with the broader Dalamud plugin ecosystem:

- [InventoryReporter](https://github.com/Alama1/InventoryReporter) provided the original inventory reporting baseline that MarketMafioso grew from.
- [Vera's Integrated World Improvements / VIWI](https://puni.sh/directory/vera/viwi) provides Workshoppa, which MarketMafioso can hand prepared workshop queues to through public Dalamud IPC.
- [Artisan](https://github.com/PunishXIV/Artisan) was a direct reference for the retainer material restock workflow.
- [ComplicatedMarketBoard](https://github.com/FranFkntastic/ComplicatedMarketBoard) and its upstream, [SimpleMarketBoard](https://github.com/Elypha/SimpleMarketBoard), were direct references for dense table and column-resizing UX choices.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for license and attribution notes.

## License

MarketMafioso does not currently declare a project-wide license.
