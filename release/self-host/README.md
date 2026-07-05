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

MarketMafioso checks `http://localhost:5088/api/capabilities` before using backend-only features such as Craft Architect quote lookup. Current Workshop Host builds advertise `craft.appraise`; older receiver-era or custom hosts that do not list it will make MMF keep using manual craft costs or Craft Architect quote-file imports.

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

The installer wizard:

- checks that Docker is installed and running;
- asks for a dashboard username;
- asks whether Workshop Host is local-only;
- creates `config\marketmafioso.env`;
- generates the plugin/server API key;
- generates the first dashboard password;
- downloads the server image;
- starts Workshop Host;
- waits for the health check;
- checks Workshop Host capabilities and quote endpoint auth/validation;
- prints the values to paste into the plugin.

Default local values:

```text
Dashboard: http://localhost:5088/
Plugin Server URL: http://localhost:5088/inventory
Database file: data\marketmafioso\marketmafioso.db
```

## Plugin Settings

After setup, copy the printed values into MarketMafioso:

```text
Server URL: http://localhost:5088/inventory
Client API Key: <generated key>
```

Open the dashboard:

```text
http://localhost:5088/
```

Log in with the generated dashboard username and password.

## Files To Keep

Do not delete:

```text
config\marketmafioso.env
data\
backups\
```

`config\marketmafioso.env` contains the generated API key and dashboard bootstrap password. `data\` contains the SQLite database. `backups\` is created by the update script.

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

Current Workshop Host builds should include `craft.appraise`. If it is missing, the host is older or custom-built without Craft Architect quote support.

The installer and updater also smoke-test `/api/craft/appraise` auth and schema validation without doing a live appraisal.

Container status:

```powershell
docker compose -f config\compose.yaml ps
```

Logs:

```powershell
docker compose -f config\compose.yaml logs marketmafioso
```
