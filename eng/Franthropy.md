# Franthropy build dependency

Outside contributors can clone https://github.com/FranFkntastic/Franthropy,
check out the full commit after `0.1.0-commit.` in
`eng/Franthropy.Version.props`, and build with
`dotnet build -c Release -p:FranthropySource=Local -p:FranthropyRoot=<absolute-source-root>`.
Pass the same properties to tests. This source route requires no private feed.

The exact package pin is owned by `eng/Franthropy.Version.props`.
Franthropy.FFXIV, .Filtering, .Web, .Observations and .AgentBridge are
published on nuget.org at that pin. Franthropy.Dalamud is not: building the
plugin in package mode needs the local feed, whose default is
`$(LOCALAPPDATA)/Franthropy/packages`; override with
`-p:FranthropyFeed=<feed-directory>`.

Workshop Host (`src/MarketMafioso.Server`) needs no Franthropy.Dalamud, so its
graph restores from nuget.org alone. The Dockerfile and the hosted publish
workflows restore it with
`--source https://api.nuget.org/v3/index.json -p:FranthropySource=Package -p:RestoreAdditionalProjectSources=`,
which keeps the local feed out; `docker build` needs no Franthropy checkout.

Workshop Host also references Craft Architect's appraisal engine,
`Gooseworks.FFXIVCraftArchitect.Core`, pinned in
`eng/CraftArchitectCore.Version.props`. That package depends on
Franthropy.FFXIV at the same pin. In local mode NuGet resolves that dependency
to the Franthropy.FFXIV project reference, so only one Franthropy.FFXIV
assembly is built and shipped; keep the two pins in step when bumping.

Release builds use packages without a source checkout. Paired development
requires both `-p:FranthropySource=Local` and
`-p:FranthropyRoot=<absolute-source-root>` on dotnet build/test.
Missing roots and unknown modes fail explicitly. Rebuild in package mode
before release packaging.
