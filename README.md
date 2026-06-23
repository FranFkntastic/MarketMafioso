# MarketMafioso

MarketMafioso is a Dalamud plugin for small, practical FFXIV quality-of-life tools.

## Features

- **Inventory Reporter** exports player and retainer inventory snapshots as JSON.
- **Retainer cache tools** help refresh and inspect the retainer data used by reports.
- **Workshop Prep** builds Free Company workshop material prep queues, checks player and retainer stock, withdraws available materials from retainers, and can send the prepared queue to VIWI Workshoppa.
- **Hosted or local receiver support** lets inventory reports be sent to a compatible endpoint.

## Usage

Open MarketMafioso in game:

```text
/mmf
```

Send an inventory report immediately:

```text
/mmf send
```

Most interaction happens in the `/mmf` window:

- Use **Inventory Reporter** to configure report endpoints, included data, and retainer cache refresh behavior.
- Use **Workshop Prep** to pick workshop projects, review material requirements, restock from retainers, and hand the queue to VIWI when ready.
- Use **Status** to check the plugin's current configuration and recent activity.

## Notes

Retainer inventory is only as fresh as the last cache refresh or retainer window scan. For the best workshop prep results, refresh the retainer cache before planning a restock.

## Acknowledgements

MarketMafioso is built in conversation with the broader Dalamud plugin ecosystem:

- [InventoryReporter](https://github.com/Alama1/InventoryReporter) provided the original inventory reporting baseline that MarketMafioso grew from.
- [Vera's Integrated World Improvements / VIWI](https://puni.sh/directory/vera/viwi) provides Workshoppa, which MarketMafioso can hand prepared workshop queues to through public Dalamud IPC.
- [Artisan](https://github.com/PunishXIV/Artisan) was a direct reference for the retainer material restock workflow.
- [ComplicatedMarketBoard](https://github.com/FranFkntastic/ComplicatedMarketBoard) and its upstream, [SimpleMarketBoard](https://github.com/Elypha/SimpleMarketBoard), were direct references for dense table and column-resizing UX choices.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for license and attribution notes.

## License

MarketMafioso does not currently declare a project-wide license.
