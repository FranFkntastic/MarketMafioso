# Market Board Approach Design

## Goal

Market Acquisition routes should open the market board automatically after world travel when a nearby market board is available. Direct interaction must happen without vnavmesh when the board is already in interaction range. vnavmesh is optional and only used to approach a visible/known board that is nearby but outside direct interaction distance.

## Pipeline

1. Route arrives on the planned world.
2. If `ItemSearch` or `ItemSearchResult` is already visible, continue to item search/probe.
3. Find the nearest targetable object named `Market Board`.
4. If the object is within direct interaction range, target it and call `TargetSystem.InteractWithObject`.
5. If the object is outside direct range but inside approach range, ask vnavmesh to move close enough to the board.
6. While vnavmesh is running, keep the route alive and wait.
7. When close enough, interact with the board and proceed to item search.
8. If no board is nearby or vnavmesh is unavailable, report a manual action status and keep waiting rather than failing the route.

## Boundaries

- vnavmesh is an optional IPC dependency, not a hard plugin load requirement.
- MarketMafioso should not bundle vnavmesh assemblies.
- Direct interaction is preferred over vnavmesh whenever possible.
- The route runner still owns route state. The approach service only opens or approaches the board.
- Purchase execution remains out of scope for this pass.

## Diagnostics

Route diagnostics should record the approach result through the route status path:

- market board UI already open
- no nearby market board target found
- direct interaction attempted
- vnavmesh unavailable
- vnavmesh path started
- vnavmesh path running
- vnavmesh path request rejected

## vnavmesh IPC

Use the published vnavmesh IPC names from `awgil/ffxiv_navmesh`:

- `vnavmesh.Nav.IsReady`
- `vnavmesh.SimpleMove.PathfindAndMoveCloseTo`
- `vnavmesh.Path.IsRunning`
- `vnavmesh.Path.Stop`

