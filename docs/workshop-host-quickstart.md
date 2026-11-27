# Workshop Host Quickstart

Workshop Host is an optional private server for MarketMafioso. It runs on your own computer and adds:

- a browser dashboard with stored inventory history,
- market acquisition work orders you can queue from the dashboard or the plugin,
- Craft Architect craft quotes inside the Acquisition Workbench,
- diagnostics you can read back later.

The plugin works without it. Install it only if you want those extras.

Everything here runs from files you can read before you run them: one Docker Compose file and one settings file. The scripts in the download are a convenience and do the same steps.

## What you need

- Windows with [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) installed and running. Linux and macOS work the same way with Docker Engine or Docker Desktop.
- Internet access from that computer. Workshop Host contacts two outside services: **Universalis** for market prices and **Garland Tools** for item and recipe data. It contacts nothing else.

Check Docker is ready in PowerShell:

```powershell
docker --version
docker compose version
```

## 1. Download

Get `marketmafioso-self-host-<version>.zip` from the [latest release](https://github.com/FranFkntastic/MarketMafioso/releases/latest) and extract it somewhere permanent. Your settings and database live in this folder.

## 2. Create the settings file

Copy `config\marketmafioso.env.example` to `config\marketmafioso.env`, open it in a text editor, and replace these two placeholders with long random strings of your own:

```text
MarketMafioso__ClientApiKey=replace-with-a-random-client-key
MarketMafioso__DashboardBootstrapPassword=replace-with-a-random-dashboard-password
```

Leave everything else as it is.

## 3. Start it

In PowerShell, from the extracted folder:

```powershell
docker compose -f config\compose.yaml up -d
```

The first start downloads the server image. When it finishes, open <http://localhost:5088/> and sign in as `marketmafioso` with the dashboard password you chose.

## 4. Connect the plugin

1. In the dashboard, open **Settings > Authentication** and press **Connect game client**. Copy the connection code.
2. In game, type `/mmf`, open **Settings > General / Server Connection**, paste the code into **Connect with code** and press **Connect**.

A code works once and expires after 10 minutes. Make a new one the same way whenever you need it.

Prefer a key over a code? Open **Manual setup** on the same plugin page and enter:

```text
Server URL: http://localhost:5088/inventory
MarketMafioso Client Key: the MarketMafioso__ClientApiKey value from config\marketmafioso.env
```

## Or use the script

Double-click `Install Workshop Host.bat`. It asks no questions and does steps 2 to 4 for you: it writes the settings file with generated secrets, starts the server, waits for it to come up, and prints the dashboard password and a plugin connection code.

The script is plain PowerShell at `scripts\Install-MarketMafiosoReceiver.ps1` if you want to read it first.

## Updating

```powershell
docker compose -f config\compose.yaml pull
docker compose -f config\compose.yaml up -d
```

Copy `data\marketmafioso\marketmafioso.db` somewhere safe first. `Update Workshop Host.bat` does the backup, pull and restart in one go.

## What to keep

| Path | What it is |
| --- | --- |
| `config\marketmafioso.env` | Your client key and dashboard password. |
| `data\` | The database, craft quote plan snapshots and the price cache. |
| `backups\` | Database copies made by the update script. |

## Craft quote plan links

A craft quote includes an "open plan" link. It opens the Craft Architect web app in your browser, and that page reads the priced plan from **your** Workshop Host. On the computer that runs Workshop Host this works as is. Your browser may ask once whether the site can reach a device on your local network; allow it.

Opening plan links from other devices needs Workshop Host reachable over HTTPS; see remote hosting below.

## Something not working

| Symptom | Check |
| --- | --- |
| `docker` commands fail | Docker Desktop is not running yet. Start it, wait until it says it is running, open a new PowerShell window. |
| Dashboard does not open | `docker compose -f config\compose.yaml ps` should show the container as running. `docker compose -f config\compose.yaml logs marketmafioso` shows why if not. |
| Port 5088 is in use | Change the left side of `"5088:8080"` in `config\compose.yaml`, and use that port everywhere above. |
| Plugin says the code is invalid | Codes expire after 10 minutes and work once. Make a new one. |
| Craft quotes fail | The computer needs to reach `universalis.app` and `www.garlandtools.org`. |

## Remote hosting

Running Workshop Host on another machine, behind HTTPS or a reverse proxy, is covered in `docs\ADVANCED-CONFIGURATION.md` inside the download and in the repository's [self-hosting guide](https://github.com/FranFkntastic/MarketMafioso/blob/main/docs/self-hosting.md).
