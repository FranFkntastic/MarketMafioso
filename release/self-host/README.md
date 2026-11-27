# MarketMafioso Workshop Host

Start here after extracting the self-hosted Workshop Host zip.

The service is still named **receiver** in some scripts, routes, Docker image names, and older docs. Receiver is the compatibility/runtime name; Workshop Host is the user-facing suite backend name.

This package is intentionally split by purpose:

```text
README.md
Install Workshop Host.bat
Update Workshop Host.bat
Install Receiver.bat
Update Receiver.bat
config/
  compose.yaml
  marketmafioso.env.example
docs/
  INSTALLATION-NOTE.md
  RECEIVER-SETTINGS.md
  ADVANCED-CONFIGURATION.md
scripts/
  Install-MarketMafiosoReceiver.ps1
  Update-MarketMafiosoReceiver.ps1
```

There is no compilable source code in this package. Workshop Host runs from the published MarketMafioso server Docker image.

## What Workshop Host Does

Workshop Host is a small private background server. It saves inventory reports in a local SQLite database and gives you a browser dashboard at `http://localhost:5088/`.

You do not need Workshop Host for basic plugin use. Install it only if you want stored inventory history, the browser dashboard, diagnostics, or suite integrations.

MarketMafioso checks `http://localhost:5088/api/capabilities` before using backend-only features such as Craft Architect quote lookup. Craft quotes run inside Workshop Host itself, so there is no extra service to install.

### Outside services

Workshop Host contacts only these outside services:

- **Universalis** (`universalis.app`) for market prices used in craft quotes and listing diagnostics.
- **Garland Tools** (`www.garlandtools.org`) for item names, recipes, and vendor data used in craft quotes and dashboard item search.

Craft quotes and dashboard item search need outbound internet access to those two services. Workshop Host never contacts a Craft Architect server. A quote's "open plan" link opens the Craft Architect web app in your browser, and that page then reads the priced plan back from your Workshop Host.

## Install Docker First

Workshop Host uses Docker so you do not have to install .NET, database tools, or web-server dependencies by hand.

On Windows:

1. Install Docker Desktop from Docker's official Windows guide:

```text
https://docs.docker.com/desktop/setup/install/windows-install/
```

2. Start **Docker Desktop** from the Start menu.
3. Wait until Docker says it is running.
4. Open PowerShell and check:

```powershell
docker --version
docker compose version
```

If those commands fail, Docker Desktop is not ready yet. Start Docker Desktop, wait for it to finish starting, then open a new PowerShell window.

## First Install

Keep this extracted folder somewhere permanent. Workshop Host stores its config and database here.

Double-click:

```text
Install Workshop Host.bat
```

The launcher runs the PowerShell installer with the right execution-policy setting for this package.

If Windows shows a warning for the downloaded script, only continue if the package came from the official MarketMafioso release page.

If you prefer PowerShell, run:

```powershell
.\scripts\Install-MarketMafiosoReceiver.ps1
```

The installer asks no questions. It:

- checks that Docker is installed and running;
- creates `config\marketmafioso.env` for local use on this computer;
- generates the plugin/server API key;
- generates the first dashboard password;
- downloads the server image;
- starts Workshop Host;
- waits for the health check;
- checks Workshop Host capabilities and quote endpoint auth/validation;
- signs in to the dashboard and prints a one-time connection code for the plugin.

The dashboard username is `marketmafioso`. For a different username or a remote address, run:

```powershell
.\scripts\Install-MarketMafiosoReceiver.ps1 -DashboardUsername <name> -PublicOrigin https://<your-host>
```

Remote hosting also needs the steps in `docs\ADVANCED-CONFIGURATION.md`.

Default local values:

```text
Dashboard: http://localhost:5088/
Plugin Server URL: http://localhost:5088/inventory
Database file: data\marketmafioso\marketmafioso.db
```

## Manual Install

If you would rather not run the script:

1. Copy `config\marketmafioso.env.example` to `config\marketmafioso.env`.
2. In `config\marketmafioso.env`, replace both placeholders with long random strings:

```text
MarketMafioso__ClientApiKey=<random-client-key>
MarketMafioso__DashboardBootstrapPassword=<random-dashboard-password>
```

3. Start Workshop Host:

```powershell
docker compose -f config\compose.yaml up -d
```

Sign in to `http://localhost:5088/` as `marketmafioso` with that password, open **Settings > Authentication** and press **Connect game client** for a connection code.

## Connect The Plugin

In game, type `/mmf`, open **Settings > General / Server Connection**, paste the connection code into **Connect with code**, then press **Connect**.

A code works once and expires after 10 minutes. To make a new one, sign in to the dashboard, open **Settings > Authentication** and press **Connect game client**.

If the code step failed or you prefer a key, open **Manual setup** on the same plugin page and enter:

```text
Server URL: http://localhost:5088/inventory
MarketMafioso Client Key: MarketMafioso__ClientApiKey from config\marketmafioso.env
```

Open the dashboard at `http://localhost:5088/` and sign in with the printed username and password.

## Files To Keep

Do not delete:

```text
config\marketmafioso.env
data\
backups\
```

`config\marketmafioso.env` contains the generated API key and dashboard bootstrap password. `data\` contains the SQLite database, craft quote plan snapshots, and the craft quote price cache. `backups\` is created by the update script.

## Updating

Double-click:

```text
Update Workshop Host.bat
```

Or run:

```powershell
.\scripts\Update-MarketMafiosoReceiver.ps1
```

The update script backs up `data\marketmafioso\marketmafioso.db`, downloads the latest server image, restarts Workshop Host, waits for the health check, and checks Workshop Host quote auth/validation.

Use:

```powershell
.\scripts\Update-MarketMafiosoReceiver.ps1 -SkipBackup
```

only if you already handled the database backup.

## More Help

Read:

```text
docs\RECEIVER-SETTINGS.md
docs\ADVANCED-CONFIGURATION.md
```

Use the advanced guide for remote hosting, HTTPS, reverse proxy, and path-prefix setup.

## Smoke Checks

Workshop Host health:

```powershell
Invoke-RestMethod -Uri http://localhost:5088/health
```

Workshop Host capabilities:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri http://localhost:5088/api/capabilities `
  -Headers @{ "X-Api-Key" = "<client-api-key>" }
```

The response should list `craft.appraise`. The installer and updater fail with a message naming what was advertised if it is missing, and they smoke-test `/api/craft/appraise` auth and schema validation without doing a live appraisal.

Container status:

```powershell
docker compose -f config\compose.yaml ps
```

Logs:

```powershell
docker compose -f config\compose.yaml logs marketmafioso
```
