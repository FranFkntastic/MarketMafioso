# MarketMafioso Workshop Host

This release includes the small Docker-based Workshop Host backend for MarketMafioso.

You only need Workshop Host if you want saved inventory history, the browser dashboard, diagnostics, or suite integrations such as Craft Architect quote lookup. The plugin itself still runs without it, but those server-backed features will not work.

Workshop Host uses Docker. If Docker is not installed yet, install Docker Desktop first:

```text
https://docs.docker.com/desktop/setup/install/windows-install/
```

Start Docker Desktop before running the setup script.

## Install

1. Download `marketmafioso-self-host-<version>.zip` from this release.
2. Extract it somewhere permanent.
3. Open the extracted folder.
4. Double-click:

```text
Install Workshop Host.bat
```

The receiver-era launcher remains available as a compatibility alias:

```text
Install Receiver.bat
```

Or run PowerShell:

```powershell
.\scripts\Install-MarketMafiosoReceiver.ps1
```

The installer asks no questions. It generates your API key and dashboard password, starts Workshop Host, and prints a one-time connection code for the plugin.

If Windows shows a warning for the downloaded script, only continue if the package came from the official MarketMafioso release page.

To install without the script, copy `config\marketmafioso.env.example` to `config\marketmafioso.env`, replace `MarketMafioso__ClientApiKey` and `MarketMafioso__DashboardBootstrapPassword` with long random strings, then run `docker compose -f config\compose.yaml up -d`.

## Connect The Plugin

In game, type `/mmf`, open **Settings > General / Server Connection**, paste the connection code into **Connect with code**, then press **Connect**.

A code works once and expires after 10 minutes. To make a new one, sign in to the dashboard, open **Settings > Authentication** and press **Connect game client**.

To use the key instead, open **Manual setup** on the same plugin page and enter `http://localhost:5088/inventory` as the Server URL and the generated key as the MarketMafioso Client Key.

Open the dashboard at `http://localhost:5088/`.

## Updating

Run this from the same extracted folder:

```text
Update Workshop Host.bat
```

or the compatibility alias:

```text
Update Receiver.bat
```

Or run PowerShell:

```powershell
.\scripts\Update-MarketMafiosoReceiver.ps1
```

The update script backs up the SQLite database, pulls the latest server image, and restarts the container.

## Keep These Files

Do not delete:

```text
config\marketmafioso.env
data\
```

`config\marketmafioso.env` contains your Workshop Host settings and API key. `data\` contains the SQLite database.
