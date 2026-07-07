# CA Appraisal Workbench Reintegration Status

Status: implemented on `local-dev`; retained as a compact maintainer note.

Craft Architect craft-cost appraisal has been moved into Acquisition Workbench, and the standalone Craft Architect Companion window has been removed from the normal plugin surface.

## Landed Shape

- `AcquisitionWorkbenchWindow` owns selected-line appraisal controls.
- `CraftAppraisalWorkbenchController` owns quote refresh, capability checks, quote clearing, threshold application, and diagnostics snapshots.
- `MarketAcquisitionDiagnosticsWindow` shows craft quote/provider diagnostics.
- `Plugin` no longer registers a standalone `CraftArchitectCompanionWindow`.
- The `MarketMafioso.CraftArchitectCompanion` service namespace remains as an internal quote/appraisal integration layer.

## Product Boundary

Craft cost is advisory evidence for an acquisition line. Route creation and purchase decisions still use the user's explicit buy threshold.

The web dashboard acquisition workflow remains first-class. The in-game workbench did not replace or demote dashboard-authored acquisition routes.

## Remaining Watch Points

- Keep user-facing labels centered on `Craft Cost`, `Craft Quote`, or `Appraisal`; avoid reviving `CA Companion` as a separate product surface.
- Keep quote-provider failures visible. Do not silently substitute stale costs unless they are labeled as last-good or stale evidence.
- Keep future Craft Architect integration work behind explicit Workshop Host capability checks or file/manual evidence paths.
