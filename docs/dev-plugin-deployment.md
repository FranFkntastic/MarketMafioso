# Dev Plugin Deployment

MarketMafioso development uses a dedicated deployed DLL as the Dalamud watched target. Do not point Dalamud at `MarketMafioso/bin/Debug/MarketMafioso.dll` or `MarketMafioso/bin/Release/MarketMafioso.dll`.

The `bin` directories are compiler outputs. Any worktree can rewrite them during a normal build, which makes them a poor place for the live plugin DLL when multiple branches or Codex worktrees are active.

## Active Target

Configure the live dev-plugin target in `MarketMafioso/dev-plugin.local.json`. This file is gitignored and should point to a stable deploy folder outside every repository and worktree `bin` directory.

Example:

```json
{
  "Configuration": "Release",
  "TargetDll": "F:\\Everything (HDD)\\Misc\\Gooseworks (Projects)\\FFXIV-Development\\_deployed\\MarketMafioso\\MarketMafioso.dll"
}
```

The exact folder can vary by machine, but the target should be a deployment artifact, not build output.

## Deploy Workflow

Run the deploy script from the worktree that should own the in-game plugin state:

```powershell
.\MarketMafioso\tools\Deploy-DevPlugin.ps1
```

The script builds the requested configuration, copies the DLL to `TargetDll`, and prints source/target verification details. Treat that deploy output as the proof that Dalamud's watched DLL was refreshed.

After a successful deploy, reload MarketMafioso in game.

## Worktree Rules

- Normal `dotnet build` output is not deployment.
- Debug appdata sync is not proof that the loaded DLL changed.
- Side/client worktrees may build and test freely, but should not share the active `TargetDll` unless they are intentionally taking over the in-game plugin.
- If `TargetDll` points inside any worktree's `bin/Debug` or `bin/Release` folder, change it before continuing plugin testing.
