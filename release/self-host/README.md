# MarketMafioso Self-Hosted Receiver

This folder is the quick-start bundle for running a private MarketMafioso backend.

It runs the server in Docker, stores data in a local SQLite database, and exposes the dashboard on port `5088` by default.

## Install

1. Install Docker Desktop or Docker Engine.
2. Extract this folder somewhere permanent.
3. Run PowerShell in this folder.
4. Run:

```powershell
.\Setup-MarketMafiosoServer.ps1
```

The setup script generates:

- the plugin/server API key,
- the dashboard password,
- `marketmafioso.env`,
- the Docker container.

At the end it prints the values to paste into the MarketMafioso plugin:

```text
Server URL: http://localhost:5088/inventory
Client API Key: <generated key>
```

Open the dashboard at:

```text
http://localhost:5088/
```

## Market Acquisition Item Search

Market Acquisition needs an XIV data gateway for item-name search and item-id resolution. During setup, provide a compatible URL when prompted.

If you leave it blank, inventory upload and browsing still work, but the dashboard item selector cannot resolve new acquisition requests by item name.

## Updating

Run:

```powershell
.\Update-MarketMafiosoServer.ps1
```

The update script backs up the SQLite database, pulls the latest server image, and restarts the container.

## Backups

Keep these:

```text
marketmafioso.env
data/
```

The SQLite database lives under:

```text
data/marketmafioso/marketmafioso.db
```

The update script writes timestamped backups to:

```text
backups/
```

## Public Hosting

For a public domain, put Caddy, nginx, Cloudflare Tunnel, or another reverse proxy in front of `http://localhost:5088`.

Use a subdomain such as:

```text
https://mmf.example.com
```

Then set the public origin during setup to that URL and configure the plugin endpoint as:

```text
https://mmf.example.com/inventory
```
