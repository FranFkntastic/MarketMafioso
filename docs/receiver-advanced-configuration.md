# Workshop Host Advanced Configuration

This guide covers Workshop Host setup beyond the local Docker quick start: reverse proxies, HTTPS, path prefixes, direct .NET hosting, and manual Docker setup.

For the normal first install, use `docs/installation.md`.

## Reverse Proxy And HTTPS

Use a reverse proxy when you want to reach Workshop Host from another machine or over the internet.

Common choices include:

- Caddy;
- nginx;
- Cloudflare Tunnel;
- another HTTPS-capable proxy you already operate.

Workshop Host should still listen locally through Docker. The proxy should handle public HTTPS and forward traffic to:

```text
http://localhost:5088
```

## Root Domain Or Subdomain

For Workshop Host at the root of a domain or subdomain:

```text
https://mmf.example.com/
```

Use:

```text
MarketMafioso__BasePath=
MarketMafioso__PublicOrigin=https://mmf.example.com
```

The plugin Server URL should be:

```text
https://mmf.example.com/inventory
```

## Path-Mounted Workshop Host

For Workshop Host hosted under a path:

```text
https://example.com/marketmafioso/
```

Use:

```text
MarketMafioso__BasePath=/marketmafioso
MarketMafioso__PublicOrigin=https://example.com
```

The plugin Server URL should be:

```text
https://example.com/marketmafioso/inventory
```

The proxy must preserve the `/marketmafioso` path when forwarding requests. If the proxy strips the path but Workshop Host expects it, dashboard routes and API calls will not line up.

## Useful Samples

Sample service and proxy files live under:

```text
docs/samples/
```

Relevant files:

```text
docs/samples/caddy.marketmafioso.example
docs/samples/nginx.marketmafioso.example
docs/samples/marketmafioso.service
docs/samples/marketmafioso.env.example
```

## Manual Docker Setup

The release bundle setup script is preferred, but manual setup is possible.

1. Copy the sample environment file:

```powershell
Copy-Item release/self-host/config/marketmafioso.env.example release/self-host/config/marketmafioso.env
```

2. Edit at least these values:

```text
MarketMafioso__ClientApiKey=<random-client-key>
MarketMafioso__DashboardBootstrapUsername=marketmafioso
MarketMafioso__DashboardBootstrapPassword=<random-dashboard-password>
```

3. Start Workshop Host:

```powershell
docker compose -f release/self-host/config/compose.yaml up -d
```

4. Check health:

```powershell
Invoke-RestMethod -Uri http://localhost:5088/health
```

## Direct .NET Hosting

Docker is the recommended setup path for most users. Direct .NET hosting is for operators who already understand service hosting.

Publish the server:

```powershell
.\src\MarketMafioso\tools\Publish-ServerRelease.ps1
```

For a self-contained Linux x64 build:

```powershell
.\src\MarketMafioso\tools\Publish-ServerRelease.ps1 -Runtime linux-x64
```

Copy the publish output to the host, then configure it with environment variables or an appsettings file based on:

```text
src/MarketMafioso.Server/appsettings.SelfHost.example.json
```

## Advanced Smoke Test

After configuring a public endpoint, check:

```powershell
Invoke-RestMethod -Uri https://mmf.example.com/health
```

For a path-mounted Workshop Host:

```powershell
Invoke-RestMethod -Uri https://example.com/marketmafioso/health
```

Then configure the plugin with the matching `/inventory` endpoint and send a report.
