# Advanced Workshop Host Configuration

Use this guide only after the local Docker install works.

## Remote Hosting

For remote access, put a reverse proxy such as Caddy, nginx, or Cloudflare Tunnel in front of:

```text
http://localhost:5088
```

## Root Domain Or Subdomain

For:

```text
https://mmf.example.com/
```

use:

```text
MarketMafioso__BasePath=
MarketMafioso__PublicOrigin=https://mmf.example.com
```

Plugin Server URL:

```text
https://mmf.example.com/inventory
```

## Path-Mounted Workshop Host

For:

```text
https://example.com/marketmafioso/
```

use:

```text
MarketMafioso__BasePath=/marketmafioso
MarketMafioso__PublicOrigin=https://example.com
```

Plugin Server URL:

```text
https://example.com/marketmafioso/inventory
```

The proxy must preserve the configured path when forwarding requests.

## Manual Commands

Start or restart:

```powershell
docker compose -f config\compose.yaml up -d
```

Pull latest image:

```powershell
docker compose -f config\compose.yaml pull
```

View logs:

```powershell
docker compose -f config\compose.yaml logs marketmafioso
```

Stop:

```powershell
docker compose -f config\compose.yaml down
```
