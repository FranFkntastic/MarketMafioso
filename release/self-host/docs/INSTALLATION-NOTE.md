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

The installer wizard generates your API key, creates the dashboard password, starts Workshop Host, and prints the plugin settings.

If Windows shows a warning for the downloaded script, only continue if the package came from the official MarketMafioso release page.

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
