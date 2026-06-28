FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MarketMafioso.sln ./
COPY MarketMafioso/MarketMafioso.csproj MarketMafioso/
COPY MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj MarketMafioso.Dashboard/
COPY MarketMafioso.Server/MarketMafioso.Server.csproj MarketMafioso.Server/
COPY MarketMafioso.Tests/MarketMafioso.Tests.csproj MarketMafioso.Tests/
COPY MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj MarketMafioso.Server.Tests/
RUN dotnet restore MarketMafioso.Server/MarketMafioso.Server.csproj

COPY . .
RUN dotnet publish MarketMafioso.Server/MarketMafioso.Server.csproj \
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
