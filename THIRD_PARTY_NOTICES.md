# Third-Party Notices

Reviewed: 2026-06-23.

This file records upstream projects that MarketMafioso was derived from, interoperates with, or used as direct implementation and design references. It is intentionally conservative: acknowledgements are included even where no license notice is strictly required.

## Direct Code Lineage

### InventoryReporter

- Repository: https://github.com/Alama1/InventoryReporter
- Role in MarketMafioso: the inventory scanner, retainer cache, JSON payload, and HTTP reporting baseline began from InventoryReporter code and has since been modified.
- License status found during review: no explicit license file or project license expression was found in the upstream repository or local clone on 2026-06-23.
- Compliance note: attribution is preserved here and in the README. Before public source or binary distribution, confirm permission/license terms with the upstream author or replace any remaining derived InventoryReporter code with independently authored code.

## Interoperability

### Vera's Integrated World Improvements / VIWI

- Plugin page: https://puni.sh/directory/vera/viwi
- Repository listing: https://puni.sh/directory/vera
- Role in MarketMafioso: Workshop Prep can send prepared queues to VIWI Workshoppa through public Dalamud IPC channels.
- License status found during review: BSD-3-Clause was provided as the VIWI source license during this feature work; the public Puni directory page reviewed here does not expose source license text.
- Code copied into MarketMafioso: none found during review.
- Compliance note: public IPC interop does not require copying VIWI source. VIWI is acknowledged because Workshop Prep depends on Workshoppa as the execution-side companion.

## Workflow And UX References

### Artisan

- Repository: https://github.com/PunishXIV/Artisan
- License found during review: BSD-3-Clause.
- Role in MarketMafioso: Artisan's restock-from-retainers workflow was used as a direct behavioral reference for Workshop Prep restock automation.
- Code copied into MarketMafioso: none found during review.

### ComplicatedMarketBoard

- Repository: https://github.com/FranFkntastic/ComplicatedMarketBoard
- Upstream repository: https://github.com/Elypha/SimpleMarketBoard
- License found during review: GPL-3.0 for ComplicatedMarketBoard and SimpleMarketBoard.
- Role in MarketMafioso: dense table UX and resizable-column behavior were used as direct UX references for the Workshop Project Browser presentation.
- Code copied into MarketMafioso: none found during review.
- Compliance note: do not copy GPL-licensed code from these projects into MarketMafioso unless MarketMafioso's project licensing and distribution plan are deliberately updated to be GPL-compatible.
