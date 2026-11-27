FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets ./
COPY eng/*.props eng/

COPY MarketMafioso.sln ./
COPY src/MarketMafioso.Contracts/MarketMafioso.Contracts.csproj src/MarketMafioso.Contracts/
COPY src/MarketMafioso/MarketMafioso.csproj src/MarketMafioso/
COPY src/MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj src/MarketMafioso.Dashboard/
COPY src/MarketMafioso.Server/MarketMafioso.Server.csproj src/MarketMafioso.Server/
COPY tests/MarketMafioso.SpecTests/MarketMafioso.SpecTests.csproj tests/MarketMafioso.SpecTests/
COPY tests/MarketMafioso.ContractTests/MarketMafioso.ContractTests.csproj tests/MarketMafioso.ContractTests/
# Franthropy and Craft Architect Core come from nuget.org; clearing
# RestoreAdditionalProjectSources keeps the developer-only local Franthropy feed out.
RUN dotnet restore src/MarketMafioso.Server/MarketMafioso.Server.csproj     --source https://api.nuget.org/v3/index.json     -p:FranthropySource=Package     -p:RestoreAdditionalProjectSources=

COPY . .
RUN dotnet publish src/MarketMafioso.Server/MarketMafioso.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

RUN mkdir -p /data
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MarketMafioso.Server.dll"]
