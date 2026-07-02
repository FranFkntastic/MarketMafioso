# PluginMaster Repository Preview

This is a local review copy for MarketMafioso's future third-party Dalamud plugin repository. Do not publish it until the release ZIP, icon URL, and public repository URL are ready.

MarketMafioso is not being submitted to the official Dalamud testing channel. The intended install path is a custom plugin repository added by the user under Dalamud settings > Experimental > Custom Plugin Repositories.

## Repository URL

When published, the user-facing repository URL should be:

```text
https://raw.githubusercontent.com/FranFkntastic/MarketMafioso/main/pluginmaster.json
```

The reviewed draft currently lives at:

```text
docs/pluginmaster/pluginmaster.preview.json
```

Before publishing, copy the reviewed JSON to repository root as:

```text
pluginmaster.json
```

## Store Entry Draft

```json
[
  {
    "Author": "FranFkntastic",
    "Name": "MarketMafioso",
    "Punchline": "Workshop logistics and self-hosted inventory history.",
    "Description": "A practical FFXIV utility plugin for Workshop Logistics and optional receiver-backed inventory history.",
    "InternalName": "MarketMafioso",
    "AssemblyVersion": "1.1.2.0",
    "RepoUrl": "https://github.com/FranFkntastic/MarketMafioso",
    "ApplicableVersion": "any",
    "DalamudApiLevel": 15,
    "Tags": [
      "inventory",
      "retainer",
      "workshop",
      "viwi",
      "utility",
      "qol",
      "export",
      "json"
    ],
    "CategoryTags": [
      "utility"
    ],
    "IsHide": false,
    "IsTestingExclusive": false,
    "DownloadLinkInstall": "https://github.com/FranFkntastic/MarketMafioso/releases/download/v1.1.2.0/MarketMafioso.zip",
    "DownloadLinkUpdate": "https://github.com/FranFkntastic/MarketMafioso/releases/download/v1.1.2.0/MarketMafioso.zip",
    "IconUrl": "https://raw.githubusercontent.com/FranFkntastic/MarketMafioso/main/docs/pluginmaster/assets/icon.png"
  }
]
```

## Public Install Summary

User-facing install text should say:

1. Open `/xlsettings`.
2. Go to **Experimental**.
3. Add the MarketMafioso repository URL under **Custom Plugin Repositories**.
4. Save.
5. Open `/xlplugins`.
6. Search for `MarketMafioso` and install it.

Do not describe dev-plugin installation as the default install path. Dev-plugin deployment is only for local contributor testing.

## Public Description

MarketMafioso is a small FFXIV quality-of-life toolbox focused on Workshop Logistics and optional private receiver support for persistent inventory history.

Keep the public feature list short:

- prepare workshop material queues;
- optionally connect to a private receiver with inventory dashboard and SQLite storage.

## Receiver Documentation

The receiver is optional but should be documented in more operational detail than the plugin features:

- Docker/Compose requirements;
- generated `config/marketmafioso.env`;
- generated client API key;
- generated dashboard credentials;
- SQLite storage location;
- local endpoint;
- reverse proxy and base-path setup;
- update script and backups;
- health and dashboard smoke checks.

The public receiver docs are:

- `docs/installation.md`
- `docs/receiver-settings.md`
- `docs/receiver-advanced-configuration.md`
- `docs/self-hosting.md`
- `release/self-host/README.md`
- `release/self-host/docs/INSTALLATION-NOTE.md`
- `release/self-host/docs/RECEIVER-SETTINGS.md`
- `release/self-host/docs/ADVANCED-CONFIGURATION.md`

## Pre-Publish Checklist

- Publish a release ZIP that contains the plugin files expected by Dalamud.
- Replace the preview download URLs if the final release tag or ZIP name changes.
- Confirm `docs/pluginmaster/assets/icon.png` is present and reachable through `IconUrl`.
- Copy `docs/pluginmaster/pluginmaster.preview.json` to root `pluginmaster.json`.
- Confirm `pluginmaster.json` is reachable by unauthenticated HTTP GET.
- Confirm the plugin appears after adding the repository URL in Dalamud Experimental settings.
- Confirm Release build succeeds from a clean checkout.
- Confirm public docs describe Workshop Logistics and receiver-backed inventory history without presenting inventory upload as useful before receiver setup.
- Confirm private notes under `.docs/private/` remain untracked.
