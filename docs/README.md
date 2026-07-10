# MarketMafioso Documentation

This directory contains public setup and operator documentation.

## Start Here

- [Installing MarketMafioso](installation.md) - plugin install flow, when Workshop Host is needed, and Windows self-host setup.
- [Workshop Host](workshop-host.md) - product boundary for the optional self-hosted backend tier.
- [Self-Hosting Workshop Host](self-hosting.md) - Docker and direct-host setup for the packaged backend.

## Operator References

- [Workshop Host Settings Reference](receiver-settings.md) - environment variables and runtime settings.
- [Workshop Host Advanced Configuration](receiver-advanced-configuration.md) - reverse proxy, HTTPS, backup, and Docker notes.
- [Receiver Backend](local-backend.md) - local backend run and dashboard notes.

## Samples

- [Caddy example](samples/caddy.marketmafioso.example)
- [Nginx example](samples/nginx.marketmafioso.example)
- [Systemd service example](samples/marketmafioso.service)
- [Environment sample](samples/marketmafioso.env.example)
- [Inventory report sample](samples/inventory-report.sample.json)

## Contributor And Release Prep

- [Dev Plugin Deployment](dev-plugin-deployment.md) - contributor workflow for deploying a local Dalamud dev plugin.
- [Hosted Workshop Host](hosted-receiver.md) - contributor notes for the dev hosted receiver environment and VPS deployment path.

- [Submission preview](pluginmaster/submission-preview.md)
- [PluginMaster preview JSON](pluginmaster/pluginmaster.preview.json)
- [Plugin icon](pluginmaster/assets/icon.png)
