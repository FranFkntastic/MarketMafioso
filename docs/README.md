# MarketMafioso Documentation

This directory contains public setup/operator docs plus a small amount of tracked implementation history. Local planning notes under `docs/design/` and `docs/superpowers/` are intentionally ignored by Git and are not part of the published documentation set.

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

## Implementation History

Files in [plans/](plans/) are tracked implementation notes or branch-recovery records. They are useful for maintainer context, but they should be reviewed before public release material points at them.

- [Automation Core Shard Restock Status](plans/2026-07-02-automation-core-shard-restock-implementation.md)
- [CA Appraisal Workbench Reintegration Status](plans/2026-07-06-ca-appraisal-workbench-reintegration-implementation.md)
- [Pruned Branch Revisit / Todo](plans/2026-07-06-pruned-branch-revisit-todo.md)
- [Retainer Restock V1 Status](plans/2026-07-07-retainer-restock-v1-implementation.md)

## Local-Only Notes

These folders are ignored by Git and are for private/local planning:

- `docs/design/`
- `docs/superpowers/`

Do not rely on those folders for public docs, release instructions, or durable cross-thread handoffs. If a handoff must survive cleanup, put it in a tracked public location after checking that it contains no private feature details.
