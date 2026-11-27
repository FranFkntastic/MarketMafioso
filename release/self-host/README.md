# MarketMafioso Workshop Host

Workshop Host is an optional private server for MarketMafioso. It adds a browser dashboard with inventory history, market acquisition work orders, Craft Architect craft quotes, and diagnostics. The plugin works without it.

The full walkthrough, with troubleshooting, is online:

```text
https://github.com/FranFkntastic/MarketMafioso/blob/main/docs/workshop-host-quickstart.md
```

Some scripts, routes and the Docker image still use the older name **receiver**. It is the same thing.

## Install by hand

You need [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) installed and running. Keep this folder somewhere permanent; your settings and database live in it.

1. Copy `config\marketmafioso.env.example` to `config\marketmafioso.env` and replace the two placeholders with long random strings of your own:

```text
MarketMafioso__ClientApiKey=replace-with-a-random-client-key
MarketMafioso__DashboardBootstrapPassword=replace-with-a-random-dashboard-password
```

2. Start it from this folder in PowerShell:

```powershell
docker compose -f config\compose.yaml up -d
```

3. Open `http://localhost:5088/` and sign in as `marketmafioso` with that password.
4. In the dashboard, open **Settings > Authentication** and press **Connect game client**. In game, type `/mmf`, open **Settings > General / Server Connection**, paste the code into **Connect with code** and press **Connect**. A code works once and expires after 10 minutes.

## Or use the script

Double-click `Install Workshop Host.bat`. It asks no questions: it writes the settings file with generated secrets, starts the server, checks it, and prints the dashboard password and a plugin connection code. The script is plain PowerShell at `scripts\Install-MarketMafiosoReceiver.ps1` if you want to read it first.

For a different dashboard username or a remote address:

```powershell
.\scripts\Install-MarketMafiosoReceiver.ps1 -DashboardUsername <name> -PublicOrigin https://<your-host>
```

Remote hosting also needs the steps in `docs\ADVANCED-CONFIGURATION.md`.

## Updating

Double-click `Update Workshop Host.bat`. It backs up the database, downloads the latest server image and restarts Workshop Host. By hand, copy `data\marketmafioso\marketmafioso.db` somewhere safe, then:

```powershell
docker compose -f config\compose.yaml pull
docker compose -f config\compose.yaml up -d
```

## Files to keep

```text
config\marketmafioso.env    your client key and dashboard password
data\                       database, craft quote plan snapshots, price cache
backups\                    database copies made by the update script
```

## Outside services

Workshop Host contacts only **Universalis** (`universalis.app`) for market prices and **Garland Tools** (`www.garlandtools.org`) for item and recipe data. Craft quotes and dashboard item search need outbound internet access to those two. A quote's "open plan" link opens the Craft Architect web app in your browser, and that page reads the priced plan from this Workshop Host.

## More

```text
docs\RECEIVER-SETTINGS.md         every setting
docs\ADVANCED-CONFIGURATION.md    remote hosting, HTTPS, reverse proxy, path prefix
```

Status and logs:

```powershell
docker compose -f config\compose.yaml ps
docker compose -f config\compose.yaml logs marketmafioso
```
