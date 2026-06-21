# AGENTS.md - MarketMafioso Agent Guide

This guide is for agentic coding tools working in this repository.

## Repository Scope
- Primary project: `MarketMafioso/`.
- Main solution: `MarketMafioso.sln`.
- The old local `Reference/` folder was intentionally left out of this cleaned repository.
- Default target for commands is the root solution or `MarketMafioso/MarketMafioso.csproj`.

## Current Product Focus (Authoritative)
- MarketMafioso currently starts from InventoryReporter behavior in almost all but name.
- Build only what is necessary to scan player inventory, cache retainer inventories, and send or preview JSON inventory snapshots.
- Treat the old active-retainer sale-listing capture and market-board query code as scrapped exploratory work.
- Plugin control should happen through the settings window and `/mmf` command.
- Keep scope tight and implementation lean. Prefer the smallest change that solves the immediate requirement.
- Retainer inventory pages (`RetainerPage1..7`) are in scope as inventory reporting data.
- Persist only plugin configuration between sessions (user options/settings).
- Persist cached retainer inventory only through existing configuration until a clearer storage model is designed.
- Future market/undercut tools should be rebuilt on top of the inventory reporting baseline.

## Tech Stack
- C# with `LangVersion` 12.
- .NET SDK in local environment (build confirmed with .NET 10 SDK + net8 plugin target).
- Plugin SDK: `Dalamud.NET.Sdk/15.0.0`.
- Nullable reference types are enabled.
- Unsafe code is enabled for game/UI interop.

## Project Layout
- `MarketMafioso/Plugin.cs`: plugin entry point, service wiring, command registration, timer lifecycle, and `WindowSystem` registration.
- `MarketMafioso/InventoryScanner.cs`: player and retainer inventory scanning.
- `MarketMafioso/RetainerCacheManager.cs`: retainer window lifecycle hooks and retainer inventory cache updates.
- `MarketMafioso/HttpReporter.cs`: JSON report creation and HTTP POST behavior.
- `MarketMafioso/InventoryPayload.cs`: outbound JSON payload contracts.
- `MarketMafioso/Windows/`: settings, cache status, send action, and JSON preview UI.
- `MarketMafioso/tools/Sync-DevPlugin.ps1`: debug artifact sync to XIVLauncher dev plugin folder.

## Build/Lint/Test Commands
Run from repository root (`MarketMafioso`).

### Restore
- `dotnet restore "MarketMafioso.sln"`

### Build
- Debug: `dotnet build "MarketMafioso.sln" -c Debug`
- Release: `dotnet build "MarketMafioso.sln" -c Release`
- Single project: `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug`
Build notes:
- Debug build runs `Sync-DevPlugin.ps1` via `AfterTargets="Build"`.
- Output folders: `MarketMafioso/bin/Debug` and `MarketMafioso/bin/Release`.
- Debug sync target defaults to `%APPDATA%\XIVLauncher\devPlugins\MarketMafioso`.

### Lint / Format
- Verify format (non-destructive):
- `dotnet format "MarketMafioso.sln" --verify-no-changes`
- Apply format:
- `dotnet format "MarketMafioso.sln"`
Lint notes:
- No repo-root `.editorconfig` was found for the active project.
- When formatter behavior is unclear, match surrounding file style.

### Tests
- Current state: no dedicated test project exists in `MarketMafioso.sln`.
- Solution-level command (safe and future-proof):
- `dotnet test "MarketMafioso.sln" -v minimal`
If tests are added later, use these patterns:
- Run all tests in one test project:
- `dotnet test "path/to/Project.Tests.csproj"`
- Run a single test method:
- `dotnet test "path/to/Project.Tests.csproj" --filter "FullyQualifiedName~Namespace.ClassName.MethodName"`
- Run by class:
- `dotnet test "path/to/Project.Tests.csproj" --filter "FullyQualifiedName~Namespace.ClassName"`
- Run by category/trait:
- `dotnet test "path/to/Project.Tests.csproj" --filter "Category=YourCategory"`

## Recommended Local Workflow
- Keep edits focused in `MarketMafioso/` unless task scope says otherwise.
- Before implementing, verify the change directly serves inventory scanning, retainer cache reporting, HTTP export, or other explicitly requested product work.
- Build after code changes: `dotnet build "MarketMafioso.sln" -c Debug`.
- Verify formatting: `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- Run tests when test projects exist or when requested.
- Validate runtime behavior by reloading plugin through XIVLauncher dev plugins.

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

## Change Boundaries and Safety
- Do not edit generated files in `obj/` or artifacts in `bin/`.
- Keep changes minimal and aligned with current architecture.
- Preserve compatibility for persisted configuration keys.
- Avoid speculative abstractions and premature architecture expansion.

## Cursor and Copilot Rules
- No `.cursor/rules/` directory was found.
- No `.cursorrules` file was found.
- No `.github/copilot-instructions.md` file was found.
- If any of these files are added later, treat them as authoritative and merge their guidance here.

## Agent Checklist
- Confirm target files are inside `MarketMafioso/` unless task says otherwise.
- Confirm the task directly supports inventory reporting, retainer cache reporting, HTTP export, or other explicitly requested product work.
- Implement changes using existing style and nullability expectations.
- Build with `dotnet build "MarketMafioso.sln" -c Debug`.
- Run `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- Run `dotnet test` when tests exist.
- Report validations performed and any limitations (for example, no test project present).
