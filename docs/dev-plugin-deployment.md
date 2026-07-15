# Dev Plugin Deployment

MarketMafioso live testing uses profile-local watched DLLs selected by exclusive machine-local lanes. Do not point Dalamud at a worktree `bin` directory or the historical shared `_deployed\MarketMafioso` target.

The `bin` directories are compiler outputs. Any worktree can rewrite them during a normal build, which makes them a poor place for the live plugin DLL when multiple branches or Codex worktrees are active.

## Active targets

The machine-local lane registry owns the active targets:

- Primary: `%APPDATA%\XIVLauncher\devPlugins\MarketMafioso\MarketMafioso.dll`
- Secondary: `%APPDATA%\XIVLauncher-Multibox-2\devPlugins\MarketMafioso\MarketMafioso.dll`

Each Dalamud profile must contain one matching MMF settings entry and one matching enabled load location. The machine-local registration normalizer maintains that invariant while both profiles are stopped.

## Deploy Workflow

Claim and deploy through the lane controller from the worktree that should own the in-game plugin state:

```powershell
$lanes = 'F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\scripts\mmf-dev-lanes\MMF-DevLane.ps1'
& $lanes -Action Status
& $lanes -Action Claim -Lane Primary -Worktree $PWD
& $lanes -Action Deploy -Lane Primary -Worktree $PWD
```

The controller rejects foreign worktrees, invokes `Deploy-DevPlugin.ps1` with the selected lane's explicit target, and then verifies the expected branch and commit through that lane's persistent Agent Bridge identity. A copied DLL without matching bridge evidence is a failed deployment.

After a successful deploy, reload MarketMafioso in game.

`Deploy-DevPlugin.ps1` and `Deploy-PluginDev.ps1` are low-level maintenance helpers, not parallel-agent entry points. Do not call them directly while lanes are configured.

When you intentionally need both sides refreshed, run the explicit combined helper:

```powershell
.\src\MarketMafioso\tools\Deploy-DevAll.ps1
```

`Deploy-DevAll.ps1` runs the server deploy first, waits for the GitHub Actions smoke checks, then deploys the local plugin DLL. Keep using the separate scripts unless you specifically want this full sequence.

For the least-decision workflow, use the changed-surface router:

```powershell
.\src\MarketMafioso\tools\Deploy-ChangedDev.ps1
```

It looks at changed files since `origin/local-dev`, plus staged, unstaged, and untracked files, then routes to the server deploy, plugin deploy, combined deploy, or no deploy. Use PowerShell's built-in `-WhatIf` to preview the chosen action:

```powershell
.\src\MarketMafioso\tools\Deploy-ChangedDev.ps1 -WhatIf
```

## Worktree Rules

- Normal `dotnet build` output is not deployment.
- Debug appdata sync is not proof that the loaded DLL changed.
- Worktrees may build and test freely, but a watched DLL may only be changed by the worktree holding that lane's claim.
- Deleted worktrees produce orphaned claims that the controller marks reclaimable instead of leaving a permanent lock.
- Release the lane when live testing is complete; do not deploy to both lanes as a convenience smoke test.
