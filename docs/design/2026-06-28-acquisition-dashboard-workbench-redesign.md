# Acquisition Dashboard Workbench Redesign

## Status

Implementation-ready design note based on `mockups/acquisition-workbench-cleanup.html`.

This document narrows the broader dashboard rebuild into one concrete pass: make the Acquisition page look and behave like the approved workbench mockup without changing the underlying market-acquisition run model.

## Current Mockup Construction

The mockup is a full-window HTML/CSS prototype, not a presentation wrapper. It models the Acquisition page as an operational workbench with two major surfaces:

- `Purchase Composer`: the left-side request-building surface.
- `Request Board`: the right-side queue, status, and request-history surface.

The top chrome is intentionally plain:

- fixed-height top bar,
- compact `MarketMafioso Dashboard` identity,
- right-aligned navigation,
- no marketing or explanatory hero content.

The mockup uses a warm, dark palette:

- near-black page background,
- brown-black panels,
- gold/accent action color,
- green live-state indicator,
- muted tan text hierarchy,
- thin warm separators.

This is meant to keep MarketMafioso visually related to Craft Architect without copying Craft Architect's stronger yellow/gold emphasis.

### Composer Layout

The composer is one framed pane with internal step bands. It deliberately avoids cards inside cards.

Step bands:

- `Target`: character, region, routing.
- `Item`: item search and lookup.
- `Buy Rules`: quantity mode, max unit price, optional max quantity, optional gil cap, HQ policy, pickup expiry.
- `Local Queue`: queued lines that have not been staged.

The composer has a sticky action footer:

- `Clear`
- local queue count
- `Add Request` or `Stage N Items`

Instructional copy is not displayed as a large info panel. Guidance is pushed into native tooltips on the fields that need it, especially quantity mode, max unit price, max quantity, and gil cap.

### Request Board Layout

The request board is one framed pane with:

- header title and active count,
- one live status toolbar,
- one refresh action,
- empty state when no requests exist,
- dense table when requests exist,
- optional selected-request detail strip for active/running states.

The table model is intentionally dense:

- item,
- quantity semantics,
- max unit,
- routing,
- status,
- latest event.

The mockup includes state buttons only as a prototype control for reviewing empty, draft, queued, and running states. They are not part of the product UI.

## Redesign Goals

1. Make the Acquisition page feel like one coherent workbench.

The current Blazor page is functional but still reads as assembled from separate component chunks. The target is a two-pane operational tool: build intent on the left, monitor execution on the right.

2. Remove cards-in-cards and nested visual boxes.

The page can have major panes, tables, drawers, and menus. It should not frame every field group as another card. Internal structure should come from spacing, section dividers, and headings.

3. Give text and fields more room to breathe.

Labels should not sit in corners or collide with typed values. Avoid floating-label behavior where MudBlazor's dynamic label positioning has already proven fragile. Prefer explicit labels above controls for dense operational forms.

4. Keep item search compact and singular.

The item selector should be labeled once. It should not receive a disproportionate amount of vertical or horizontal emphasis compared with the other required fields.

5. Clarify acquisition semantics through labels, not tutorial panels.

`AllBelowThreshold` should read as `All below threshold` in controls and `All safe stock` in queue/request summaries. Optional caps should be visibly optional. Required safety thresholds should read as required through labels, validation, and disabled actions.

6. Keep dashboard character targeting account-aware.

The target character must remain a dropdown backed by account-associated characters. It must not regress into a free-typed character/world pair.

7. Consolidate live status and refresh.

There should be one obvious live-update status and one obvious refresh affordance. Duplicate refresh buttons make it unclear which state is stale.

8. Preserve dense table affordances.

Request and inventory tables should keep useful operational features such as sorting, filtering, resizing, and clear column separators. The redesign should improve visual framing without replacing the table with a static mockup-only surface.

9. Use theme tokens instead of local hardcoded color decisions.

Colors should come from `DashboardThemeService` and semantic CSS variables, so future theme work is not a search-and-replace pass across components.

## Non-Goals

- Redesigning the plugin ImGui window.
- Changing market-board automation behavior.
- Changing batch, attempt, or route execution semantics.
- Reintroducing CSRF.
- Reintroducing `/api/marketmafioso` compatibility routes.
- Building public multi-user hosting.
- Moving Craft Architect off the domain root.

## Current Implementation Surface

Primary files:

- `MarketMafioso.Dashboard/Pages/Home.razor`
  - owns the Acquisition page route, login gate, SSE startup, live status text, and composition of builder/grid/detail drawer.
- `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
  - owns local draft state, character selection, item lookup, line queueing, and batch staging.
- `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
  - owns the request table, row selection, status chips, cancel/resend/template actions, and manual refresh event.
- `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
  - owns selected request detail, attempt/timeline display, and secondary request actions.
- `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`
  - owns the current live-update status strip.
- `MarketMafioso.Dashboard/Services/DashboardThemeService.cs`
  - owns the warm dark theme and exported CSS variables.
- `MarketMafioso.Dashboard/wwwroot/css/app.css`
  - owns the current dashboard shell, pane, form, table, and MudBlazor override styling.

The current implementation already has useful pieces:

- account-scoped character dropdown,
- explicit-label fields in parts of `RequestBuilder`,
- central theme service,
- server request grid with resizable columns,
- SSE-driven request refresh,
- terminal request rows preserved in the table.

The main gap is product fit and polish: the real page still has cramped text, uneven component weight, a visible helper alert, duplicate refresh affordances, and a weaker visual hierarchy than the mockup.

## Implementation Plan

### Phase 1: Treat The Mockup As The Component Contract

Acceptance criteria:

- `mockups/acquisition-workbench-cleanup.html` remains the visual reference.
- The Blazor page names and hierarchy align with the mockup's `Purchase Composer` and `Request Board` model.
- Prototype-only state controls from the mockup are not implemented in the real dashboard.

Steps:

1. Rename user-facing `New Purchase Request` heading to `Purchase Composer`.
2. Keep `Request Queue` or change it to `Request Board`; prefer `Request Board` if the surface contains both active and terminal rows.
3. Confirm `Home.razor` page composition maps to:
   - page title,
   - composer pane,
   - request board pane,
   - details drawer.
4. Avoid adding a third always-visible operational band unless it belongs in one of those panes.

### Phase 2: Rework Acquisition Page Shell

Acceptance criteria:

- The Acquisition page has one compact title row.
- Live status is integrated into the request-board toolbar or a single top operational strip.
- There is exactly one refresh action for request state.
- The two-column layout gives the composer a stable width and the board the remaining width.
- The page does not crop its lower controls on normal desktop viewports.

Files:

- `MarketMafioso.Dashboard/Pages/Home.razor`
- `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`
- `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- `MarketMafioso.Dashboard/wwwroot/css/app.css`

Steps:

1. Move request active count and refresh into `ServerRequestGrid` header/toolbar.
2. Remove or collapse the separate full-width `LiveStatusStrip` if its content can be represented in the request-board toolbar.
3. Keep logout in the page title row or top chrome, not inside the workbench grid.
4. Set the acquisition grid to a composer width near `minmax(390px, 470px)` and a board width of `minmax(620px, 1fr)`.
5. Use internal scrolling only where necessary:
   - composer form body,
   - board table body,
   - details drawer.

### Phase 3: Flatten The Composer

Acceptance criteria:

- The composer is one framed pane.
- Internal groups are section bands, not nested cards.
- Labels have consistent spacing and never collide with typed values.
- The item selector is labeled once.
- The visible helper alert is removed.
- Field guidance is available through tooltips or helper icons.

Files:

- `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- `MarketMafioso.Dashboard/wwwroot/css/app.css`

Steps:

1. Replace remaining `MudAlert help-alert` guidance with tooltips or small icon affordances on relevant labels.
2. Keep explicit labels above all builder inputs and selects.
3. Ensure no builder control uses a floating label when a value may overlap with it.
4. Make `Item` a normal section with one `MudAutocomplete`, one label, and no additional boxed treatment.
5. Add section separators through `.builder-section + .builder-section`, not separate panel backgrounds.
6. Convert action row to the mockup structure:
   - left `Clear`,
   - center local queue count,
   - right `Add Request` or `Stage Queue`.
7. Keep the queued-lines table compact and visually subordinate to the composer action flow.

### Phase 4: Tighten Quantity And Validation Semantics

Acceptance criteria:

- `AllBelowThreshold` appears as `All below threshold` in the editor.
- Queue and request rows show this as `All safe stock` when there is no max quantity.
- `Max unit price` is visibly required.
- `Max quantity` and `Gil cap` are visibly optional.
- `Stage Queue` is disabled unless at least one queued line is valid.
- Validation errors are specific and do not clear typed input.

Files:

- `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- `MarketMafioso.Dashboard/Models/DashboardUiModels.cs`

Steps:

1. Add small label markers or placeholders for `Required` and `Optional`.
2. Keep `TargetQuantity` support, but do not reintroduce `Exact` or `UpTo`.
3. Ensure `AllBelowThreshold` sends `MaxQuantity = 0` when max quantity is blank.
4. Ensure queue display reads:
   - `All safe stock` when max quantity is blank,
   - `Max N` when max quantity is present.
5. Ensure request grid uses the same display logic.

### Phase 5: Rebuild The Request Board Around Operational State

Acceptance criteria:

- Empty state is calm and centered.
- Active/terminal request rows share one dense table.
- Table columns remain resizable.
- Filter icons stay visible where they are useful, but do not dominate the table header.
- The selected-request drawer remains the place for deep timeline/attempt details.

Files:

- `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
- `MarketMafioso.Dashboard/wwwroot/css/app.css`

Steps:

1. Update the grid header to show:
   - request-board title,
   - active count,
   - live connection state,
   - last update,
   - single refresh button.
2. Preserve `MudDataGrid` resizable columns.
3. Keep actions in a menu rather than expanding all actions into separate buttons.
4. Use status chips with warm-theme-compatible colors.
5. Keep selected-row styling subtle.

### Phase 6: Theme And CSS Cleanup

Acceptance criteria:

- Acquisition colors come from `DashboardThemeService` and `--mmf-*` variables.
- The page reads warmer than the earlier blue-heavy build.
- CSS does not globally compress MudBlazor inputs in a way that causes label overlap.
- Component-specific overrides are scoped to acquisition classes where possible.

Files:

- `MarketMafioso.Dashboard/Services/DashboardThemeService.cs`
- `MarketMafioso.Dashboard/wwwroot/css/app.css`

Steps:

1. Keep the current warm palette as the default theme.
2. Add any missing semantic variables before introducing new hardcoded colors.
3. Scope dense form overrides under `.builder-panel` or `.acquisition-page`.
4. Scan CSS for unnecessary hardcoded hex values after the pass.
5. Preserve shared inventory/settings styles unless the selector is clearly acquisition-only.

### Phase 7: Settings And Navigation Guardrails

Acceptance criteria:

- The `Settings` nav item remains under the MarketMafioso base path.
- The Acquisition page does not navigate to Craft Architect when opening Settings.
- Default character settings remain available in Settings.
- Character selection in Acquisition uses account-associated characters only.

Files:

- `MarketMafioso.Dashboard/Layout/MainLayout.razor`
- `MarketMafioso.Dashboard/Pages/Settings.razor`
- `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`

Steps:

1. Verify navigation uses relative routes that respect `/marketmafioso/`.
2. Verify direct loads work for:
   - `/marketmafioso/`
   - `/marketmafioso/acquisition`
   - `/marketmafioso/settings`
3. Ensure Settings exposes default character, default region, default routing, and pickup expiry.
4. Ensure Acquisition consumes those settings on page load.

### Phase 8: Verification

Acceptance criteria:

- Dashboard builds.
- Server builds.
- Focused server/dashboard tests pass where affected.
- Deployed dev dashboard visually matches the mockup direction closely enough for review.
- Browser console has no MarketMafioso-owned errors during basic acquisition workflow.

Commands:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
dotnet build "MarketMafioso.Server/MarketMafioso.Server.csproj" -c Debug
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Dev deployment, when the pass is ready for live review:

```powershell
& "MarketMafioso/tools/Deploy-ServerDev.ps1" -Ref main -TimeoutSeconds 900
```

Browser smoke:

- Log in at `/marketmafioso/`.
- Confirm Acquisition loads without redirecting to Craft Architect.
- Search for an item.
- Add one line to local queue.
- Stage the queue.
- Confirm the request appears without manual page refresh.
- Cancel/resend/use-as-template from the action menu.
- Open Settings and return to Acquisition.

## Risks And Constraints

- MudBlazor floating-label and dense-input defaults can fight the desired compact layout. Use explicit labels and scoped CSS instead of squeezing component internals globally.
- `MudDataGrid` gives sorting/filtering/resizable columns but can be visually heavy. Preserve its behavior first, then style around it.
- The dashboard now has multiple rebuilt pages. Shared CSS changes can regress Inventory, Overview, Settings, or Diagnostics if selectors are too broad.
- SSE currently owns live request updates. The design should not add polling as a second competing refresh model.
- The mockup is intentionally static. Where the static markup conflicts with working accessibility or MudBlazor behavior, preserve functionality and document the visual compromise.

## Commit Strategy

Prefer small commits:

1. `docs: capture acquisition workbench redesign`
2. `style: align acquisition shell with workbench mockup`
3. `style: flatten acquisition composer`
4. `fix: tighten acquisition quantity displays`
5. `style: polish request board`
6. `fix: keep dashboard settings navigation under base path`

Each commit should leave the dashboard buildable.

## Done Criteria

The pass is done when:

- the dev dashboard visually follows the workbench mockup,
- no nested-card builder structure remains,
- the helper infobox is gone,
- Acquisition has one refresh surface,
- character selection is a dropdown,
- item search remains functional,
- queued multi-line staging still works,
- live request updates still arrive without a page refresh,
- Settings is reachable under `/marketmafioso/settings`,
- the verification commands above have passed or any skipped command is explicitly explained.
