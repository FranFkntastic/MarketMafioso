# AGENTS.md - MarketMafioso Agent Guide

This guide is for agentic coding tools working in this repository.

## Scope
- Primary plugin: `MarketMafioso/`.
- Local receiver: `MarketMafioso.Server/`.
- Plugin tests: `MarketMafioso.Tests/`.
- Server tests: `MarketMafioso.Server.Tests/`.
- Main solution: `MarketMafioso.sln`.
- Default command target: the solution, or `MarketMafioso/MarketMafioso.csproj` for plugin-only work.

## Product Boundaries
- Current first-class features are Inventory Reporter and Workshop Prep.
- Inventory Reporter owns player inventory scans, retainer cache scans, JSON preview, and HTTP export.
- Workshop Prep owns project selection, prep queue state, material requirements, retainer availability, VIWI queue handoff, retainer material withdrawal, and native execution of MarketMafioso-owned prep queues.
- MarketMafioso may execute its own Workshop Prep queue, but must not mirror VIWI Workshoppa's full module surface or treat Workshoppa state as source of truth.
- Treat old market-board, sale-listing, and undercut experiments as out of scope unless explicitly requested.
- Plugin control should stay GUI-first through `/mmf`; add slash commands only when requested.
- Keep changes lean and aligned with the existing module shape.
- Persist user configuration and intentional prep queue state. Do not persist transient UI automation progress.

## Project Layout
- `MarketMafioso/Plugin.cs`: plugin entry point, service wiring, command registration, timer lifecycle, and `WindowSystem` registration.
- `MarketMafioso/InventoryScanner.cs`: player and retainer inventory scanning.
- `MarketMafioso/RetainerCacheManager.cs`: retainer window lifecycle hooks and retainer inventory cache updates.
- `MarketMafioso/HttpReporter.cs`: JSON report creation and HTTP POST behavior.
- `MarketMafioso/InventoryPayload.cs`: outbound JSON payload contracts.
- `MarketMafioso/WorkshopPrep/`: workshop project catalog, availability math, VIWI IPC, retainer restock automation, and native assembly automation.
- `MarketMafioso/Windows/`: settings, inventory reporter, workshop prep, cache status, send action, and JSON preview UI.
- `MarketMafioso/tools/Sync-DevPlugin.ps1`: debug artifact sync to XIVLauncher dev plugin folder.
- `MarketMafioso/tools/Deploy-DevPlugin.ps1`: explicit dev-plugin deployment and active target verifier.
- `MarketMafioso/dev-plugin.local.json`: gitignored local config for the dedicated deployed DLL path Dalamud actually loads.
- `MarketMafioso.Server/`: local ASP.NET inventory-report receiver and dashboard.
- `docs/`: maintainer documentation and design notes.

## Tech Stack
- C# with `LangVersion` 12.
- Plugin target: `net8.0-windows`.
- Plugin SDK: `Dalamud.NET.Sdk/15.0.0`.
- Nullable reference types and unsafe code are enabled in the plugin.

## Build/Lint/Test Commands
Run from repository root (`MarketMafioso`).

### Restore
- `dotnet restore "MarketMafioso.sln"`

### Build
- Debug: `dotnet build "MarketMafioso.sln" -c Debug`
- Release: `dotnet build "MarketMafioso.sln" -c Release`
- Single project: `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug`
- Local backend: `dotnet run --project "MarketMafioso.Server" --urls http://localhost:8080`
Build notes:
- Debug build runs `Sync-DevPlugin.ps1` via `AfterTargets="Build"`.
- Output folders: `MarketMafioso/bin/Debug` and `MarketMafioso/bin/Release`; these are compiler outputs only and should not be configured as long-lived Dalamud watched targets.
- Debug sync target defaults to `%APPDATA%\XIVLauncher\devPlugins\MarketMafioso`.
- Active dev-plugin deployment should use a dedicated folder outside every repo/worktree `bin/` directory. Set `TargetDll` in `MarketMafioso/dev-plugin.local.json` to that deployed DLL, not to `MarketMafioso/bin/Debug` or `MarketMafioso/bin/Release`.
- Use `MarketMafioso/tools/Deploy-DevPlugin.ps1` to build, copy, and verify the active DLL. Appdata debug sync, normal build output, and `bin/Release` timestamps are not proof that the loaded plugin was refreshed.
- See `docs/dev-plugin-deployment.md` for the current local deployment workflow.

### Lint / Format
- Verify format (non-destructive):
- `dotnet format "MarketMafioso.sln" --verify-no-changes`
- Apply format:
- `dotnet format "MarketMafioso.sln"`
Lint notes:
- No repo-root `.editorconfig` was found for the active project.
- When formatter behavior is unclear, match surrounding file style.

### Tests
- All tests: `dotnet test "MarketMafioso.sln" -c Debug -v minimal`
- Plugin tests: `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal`
- Server tests: `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal`
- Run all tests in one test project:
- `dotnet test "path/to/Project.Tests.csproj"`
- Run a single test method:
- `dotnet test "path/to/Project.Tests.csproj" --filter "FullyQualifiedName~Namespace.ClassName.MethodName"`
- Run by class:
- `dotnet test "path/to/Project.Tests.csproj" --filter "FullyQualifiedName~Namespace.ClassName"`
- Run by category/trait:
- `dotnet test "path/to/Project.Tests.csproj" --filter "Category=YourCategory"`

## Recommended Local Workflow
- Keep edits focused in the relevant module unless task scope says otherwise.
- Before implementing, verify the change directly serves inventory reporting, retainer cache reporting, HTTP export, workshop prep, VIWI handoff, retainer restock automation, or other explicitly requested product work.
- Build after code changes: `dotnet build "MarketMafioso.sln" -c Debug`.
- Verify formatting: `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- Run relevant tests after code changes.
- For plugin behavior changes, run `MarketMafioso/tools/Deploy-DevPlugin.ps1` from the intended active worktree, verify the reported source and target hashes match, then reload the plugin in game.
- Side/client worktrees should build and test normally, but should not deploy to the active Dalamud target unless intentionally configured to own that deployment.

### Fast Patch Lane
Use this lane for tiny, speculative client/plugin changes when the user is actively testing in game, especially Market Acquisition UI automation patches.

- Read only the exact source file and latest route log needed to understand the failure.
- Patch the smallest observable decision point.
- Prefer focused compile/tests only; skip format/full solution verification until the behavior is confirmed unless the diff is broad or risky.
- Deploy with `MarketMafioso/tools/Deploy-DevPlugin.ps1` and report the visible manifest version plus verified target DLL hash.
- Hand off for live testing before commit/push when the patch is speculative or likely to need another iteration.
- After the user confirms the behavior, commit/push and optionally redeploy from the committed head so artifact metadata matches source control.

Use the durable workflow instead for server/dashboard/auth/persistence changes, purchase-safety changes, routing semantics, or any change where uncommitted behavior would be hard to reconstruct safely.

## Code Style Guidelines
Follow established patterns already present in `MarketMafioso/`.

### Imports and Namespaces
- Use file-scoped namespaces (`namespace MarketMafioso;` or `namespace MarketMafioso.Windows;`).
- Keep `using` directives at top of file.
- Keep import ordering stable with nearby files.
- Existing files often place framework/project namespaces before `System` namespaces.
- Remove unused `using` directives.

### Class and Member Design
- Prefer `sealed` classes for concrete implementations.
- Use explicit access modifiers on all members.
- Keep constructors focused on dependency wiring.
- Keep services and windows single-purpose.
- Use small enums and DTO-like models for cross-layer contracts.

### Formatting and Layout
- Use 4-space indentation.
- Place opening braces on new lines.
- Keep long interpolated strings readable by splitting when needed.
- Use expression-bodied members only for very small pass-through methods.
- Prefer object/collection initializers for structured setup.

### Types and Nullability
- Respect nullable annotations and guard null-sensitive paths.
- Avoid fallback/default substitution for required data paths; fail fast with explicit errors instead.
- Use `var` when RHS type is obvious; otherwise use explicit types.
- Preserve numeric intent (`uint`, `ulong`, `int`) in API-facing code.

### Naming Conventions
- Types/methods/properties/enums: PascalCase.
- Private fields: `_camelCase`.
- Locals/parameters: camelCase.
- Async methods end with `Async`.
- Boolean names should read like predicates (`isReady`, `hasPlan`, `canStart`).

### Error Handling and Logging
- Use guard clauses for invalid state and inputs.
- Wrap event handlers, callbacks, and IPC boundaries in `try/catch`.
- Never swallow exceptions silently.
- Do not introduce fallback behavior for missing required data; throw explicit exceptions and log the failure.
- Log via Dalamud's `IPluginLog` directly (no proprietary logging infrastructure).
- Log level guidance: `Verbose` (diagnostics), `Info` (lifecycle), `Warn` (recoverable), `Error` (failures).
- Prefer explicit exception types and clear messages for invariant failures.

### Async, Threading, and Cancellation
- Thread `CancellationToken` through cancellable async flows.
- Call `token.ThrowIfCancellationRequested()` in long-running loops.
- Use `ConfigureAwait(false)` in service-layer awaits.
- Protect shared mutable state with existing `lock` patterns.
- Avoid blocking waits (`.Wait()`, `.Result`) in plugin logic.

### Dalamud / Plugin Patterns
- Subscribe to Dalamud/game events during init and unsubscribe in `Dispose()`.
- Keep unsafe interop narrowly scoped and defensive.
- Validate addon/agent pointers before dereferencing.
- Prefix chat output with `[MarketMafioso]` for user-facing consistency.
- Prefer GUI-first plugin control. Do not expand or reintroduce slash-command/automation surface area unless it is explicitly requested.

### ImGui Window Patterns
- Prefer `Dalamud.Interface.Windowing.Window` + `WindowSystem` for top-level plugin windows.
- Use native window collapse behavior; do not implement custom collapse/expand buttons for top-level windows.
- Use `DrawConditions()` for visibility gates and `PreDraw()` to apply dynamic positioning/size/collapse state.
- Keep `Draw()` deterministic and side-effect aware.
- Persist config immediately after user-driven setting changes.
- Prefer readable tables for dense data.
- Keep per-window selection/filter state local.
- Avoid unnecessary allocations in per-frame hot paths.
- Use shared helpers such as `ImGuiUi` for repeated UI idioms.
- Dense browser tables should have sensible default widths and resizable columns.

## Change Boundaries and Safety
- Do not edit generated files in `obj/` or artifacts in `bin/`.
- Do not commit local backend data under `MarketMafioso.Server/data/`.
- Keep changes minimal and aligned with current architecture.
- Preserve compatibility for persisted configuration keys.
- Avoid speculative abstractions and premature architecture expansion.

## Agent Checklist
- Confirm target files are inside `MarketMafioso/` unless task says otherwise.
- Confirm the task directly supports current product scope or an explicitly requested expansion.
- Implement changes using existing style and nullability expectations.
- Build with `dotnet build "MarketMafioso.sln" -c Debug`.
- Run `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- Run relevant tests.
- If plugin behavior changes, confirm `Deploy-DevPlugin.ps1` refreshed the dedicated configured Dalamud target DLL; do not accept appdata sync or `bin/Release` timestamps as proof.
- Report validations performed and any limitations.
