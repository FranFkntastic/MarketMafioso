# Installing MarketMafioso

MarketMafioso has two pieces:

- the Dalamud plugin client, installed through a third-party plugin repository;
- optional private Workshop Host, installed separately when you want saved inventory history and the browser dashboard.

Workshop Host is the self-hosted backend tier for private state and automation. Its current MarketMafioso runtime is still named **receiver** in scripts, routes, and Docker image names for compatibility. The plugin can open without Workshop Host, and Workshop Logistics can be used locally. Inventory history, dashboard browsing, and report uploads require Workshop Host.

For the architecture boundary between standalone plugin features, Craft Architect quote-file handoffs, and self-hosted automation, see `docs/workshop-host.md`.

## Install The Plugin Client

MarketMafioso should be installed like a normal third-party Dalamud plugin: add the FranFkntastic plugin repository URL in Dalamud's Experimental settings, then install it from the plugin installer.

Use this repository URL:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

In game:

1. Open Dalamud settings with `/xlsettings`.
2. Open the **Experimental** tab.
3. Find **Custom Plugin Repositories**.
4. Paste the MarketMafioso repository URL into an empty repository field.
5. Add/save the repository.
6. Open the plugin installer with `/xlplugins`.
7. Search for `MarketMafioso` and install it.
8. Open MarketMafioso with `/mmf`.

Until the repository URL and release ZIP are published, MarketMafioso is not available through the standard third-party install path. Development plugin builds are only for local contributor testing and are documented separately in `docs/dev-plugin-deployment.md`.

## Decide Whether You Need Workshop Host

You do not need Workshop Host for basic plugin use.

Use Workshop Host if you want:

- stored inventory snapshots;
- a browser dashboard for inventory history;
- local control of the database and backups.
- future self-hosted automation and cross-tool state under the Workshop Host tier.

Skip Workshop Host only if you want Workshop Logistics and do not care about inventory history or dashboard storage.

## Download The Workshop Host Package

Skip this section if you only want the plugin.

For the Workshop Host dashboard:

1. Open the MarketMafioso GitHub **Releases** page: `https://github.com/FranFkntastic/MarketMafioso/releases`.
2. Download `marketmafioso-self-host-<version>.zip` from the latest release.
3. Right-click the zip and choose **Extract All**.
4. Move the extracted folder somewhere permanent, such as `Documents\MarketMafioso Workshop Host`.

Do not run Workshop Host from inside the zip preview window. Extract it first so it can keep its config, database, and backups next to the installer.

## Install Docker Desktop

Workshop Host runs in Docker. Docker is the app that keeps it packaged so you do not need to install .NET, database tools, or web-server dependencies by hand.

On Windows, the easiest path is Docker Desktop:

1. Download Docker Desktop for Windows from Docker's official install page: `https://docs.docker.com/desktop/setup/install/windows-install/`.
2. Run the installer.
3. Accept the WSL 2 option if Docker asks for it.
4. Restart Windows if the installer asks.
5. Open **Docker Desktop** from the Start menu.
6. Wait until Docker says it is running.

To check Docker from PowerShell:

```powershell
docker --version
docker compose version
```

If either command is not found, Docker Desktop is not installed, is not running, or PowerShell was opened before Docker finished installing. Start Docker Desktop, open a new PowerShell window, and try again.

## Install Workshop Host

Download or extract the self-hosted Workshop Host bundle, then keep it in a permanent folder. Do not run it from a temporary downloads folder if you care about keeping stored inventory history.

The bundle folder is:

```text
release/self-host/
```

Open the extracted folder and double-click:

```text
Install Workshop Host.bat
```

That launcher runs the PowerShell installer with the right execution-policy setting for this package.

If Windows shows a warning for the downloaded script, only continue if the package came from the official MarketMafioso release page.

If you prefer PowerShell, open PowerShell in the extracted folder and run:

```powershell
.\scripts\Install-MarketMafiosoReceiver.ps1
```

The installer wizard will:

- confirm Docker is available;
- ask for a dashboard username;
- ask whether Workshop Host is local-only;
- create `config\marketmafioso.env`;
- generate a private plugin API key;
- generate the first dashboard password;
- download the MarketMafioso server image;
- start the Workshop Host container;
- wait for the health check;
- print the values you need for the plugin and dashboard.

Default local addresses:

```text
Dashboard: http://localhost:5088/
Plugin server URL: http://localhost:5088/inventory
```

Default local storage:

```text
release/self-host/config/marketmafioso.env
release/self-host/data/marketmafioso/marketmafioso.db
```

`config/marketmafioso.env` contains generated secrets. `data/marketmafioso/marketmafioso.db` is the Workshop Host database. Keep both.

## Connect The Plugin To Workshop Host

In `/mmf` settings, set:

```text
Server URL: http://localhost:5088/inventory
Client API Key: <generated client API key>
```

The installer prints the generated client API key. It is also saved as `MarketMafioso__ClientApiKey` in `config/marketmafioso.env`.

Open the dashboard in your browser:

```text
http://localhost:5088/
```

Use the dashboard username and password printed by the installer. They are also saved in `config/marketmafioso.env`.

## Workshop Host Settings

The generated `config/marketmafioso.env` is the Workshop Host configuration file. Most users should leave it alone after setup, but it is important because it contains the API key, dashboard login bootstrap values, storage location, and retention limits.

Detailed setting explanations live in:

```text
docs/receiver-settings.md
```

The most important settings are:

```text
MarketMafioso__ClientApiKey
MarketMafioso__DatabasePath
MarketMafioso__SnapshotRetentionCount
MarketMafioso__RequireDashboardAuth
MarketMafioso__DashboardBootstrapUsername
MarketMafioso__DashboardBootstrapPassword
```

## Updating Workshop Host

From the Workshop Host bundle:

Double-click:

```text
Update Workshop Host.bat
```

Or run PowerShell:

```powershell
.\scripts\Update-MarketMafiosoReceiver.ps1
```

The update script backs up the SQLite database, downloads the latest server image, restarts the container, waits for the health check, and checks Workshop Host quote auth/validation.

## Backups

Back up:

```text
release/self-host/config/marketmafioso.env
release/self-host/data/
release/self-host/backups/
```

Losing `config/marketmafioso.env` means you may need to reconfigure the plugin API key and dashboard login. Losing `data/` means losing stored Workshop Host history.

## Smoke Checks

Check Workshop Host health:

```powershell
Invoke-RestMethod -Uri http://localhost:5088/health
```

Check Docker container status:

```powershell
docker compose -f config\compose.yaml ps
```

Check logs from the Workshop Host bundle folder:

```powershell
docker compose -f config\compose.yaml logs marketmafioso
```

If the plugin cannot send data:

- confirm Docker Desktop is running;
- confirm the Workshop Host container is running;
- confirm the Server URL ends with `/inventory`;
- confirm the plugin Client API Key matches `MarketMafioso__ClientApiKey`;
- confirm `http://localhost:5088/health` works in the same Windows session.

## Advanced Hosting

Remote access, reverse proxies, HTTPS, path prefixes, and direct .NET hosting are advanced topics. They live in:

```text
docs/receiver-advanced-configuration.md
```
