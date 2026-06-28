# MarketMafioso Self-Hosted Backend

This release includes a small Docker-based backend for MarketMafioso.

You only need this backend if you want persistent Inventory Reporter storage, the browser dashboard, diagnostics, or Market Acquisition dashboard requests. The plugin itself still runs without it, but those server-backed features will not work.

## Install

1. Install Docker Desktop or Docker Engine.
2. Download `marketmafioso-self-host-<version>.zip` from this release.
3. Extract it somewhere permanent.
4. Open PowerShell in the extracted folder.
5. Run:

```powershell
.\Setup-MarketMafiosoServer.ps1
```

The setup script generates your API key, creates the dashboard password, starts the server, and prints the plugin settings.

Paste these into the MarketMafioso plugin settings:

```text
Server URL: http://localhost:5088/inventory
Client API Key: the generated API key
```

Open the dashboard at:

```text
http://localhost:5088/
```

## Updating

Run this from the same extracted folder:

```powershell
.\Update-MarketMafiosoServer.ps1
```

The update script backs up the SQLite database, pulls the latest server image, and restarts the container.

## Keep These Files

Do not delete:

```text
marketmafioso.env
data/
```

`marketmafioso.env` contains your server settings and API key. `data/` contains the SQLite database.

## Market Acquisition Item Search

Market Acquisition needs a compatible XIV data endpoint for item-name search and item-id resolution. If you leave that setting blank during setup, inventory upload and browsing still work, but creating acquisition requests by item name will not.
