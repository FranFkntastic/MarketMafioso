FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG CRAFT_ARCHITECT_CORE_PROJECT="/src/craft-architect/src/FFXIV Craft Architect.Core/FFXIV Craft Architect.Core.csproj"

COPY MarketMafioso.sln ./
COPY ["craft-architect/src/FFXIV Craft Architect.Core/FFXIV Craft Architect.Core.csproj", "craft-architect/src/FFXIV Craft Architect.Core/"]
COPY src/MarketMafioso/MarketMafioso.csproj src/MarketMafioso/
COPY src/MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj src/MarketMafioso.Dashboard/
COPY src/MarketMafioso.Server/MarketMafioso.Server.csproj src/MarketMafioso.Server/
COPY tests/MarketMafioso.Tests/MarketMafioso.Tests.csproj tests/MarketMafioso.Tests/
COPY tests/MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj tests/MarketMafioso.Server.Tests/
RUN dotnet restore src/MarketMafioso.Server/MarketMafioso.Server.csproj \
    /p:CraftArchitectCoreProject="$CRAFT_ARCHITECT_CORE_PROJECT"

COPY . .
COPY ["craft-architect/src/FFXIV Craft Architect.Core/", "craft-architect/src/FFXIV Craft Architect.Core/"]
RUN dotnet publish src/MarketMafioso.Server/MarketMafioso.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:CraftArchitectCoreProject="$CRAFT_ARCHITECT_CORE_PROJECT" \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

RUN mkdir -p /data
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MarketMafioso.Server.dll"]
