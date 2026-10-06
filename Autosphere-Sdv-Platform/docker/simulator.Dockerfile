# syntax=docker/dockerfile:1
# Standalone ECU simulator (optional). Use with CanBus__Transport=SocketCan on a Linux host with vcan0
# (network_mode: host), or CanBus__Transport=Udp to bridge to a separately running gateway.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/shared/ src/shared/
COPY src/gateway/ src/gateway/
COPY src/simulator/ src/simulator/
RUN dotnet publish src/simulator/AutoSphere.VehicleSimulator/AutoSphere.VehicleSimulator.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "AutoSphere.VehicleSimulator.dll"]
