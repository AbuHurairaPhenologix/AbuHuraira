# syntax=docker/dockerfile:1
# AutoSphere vehicle edge gateway (.NET worker). Debian-based runtime so the SocketCAN transport
# (glibc) is usable when the container is run on a Linux host with a CAN interface.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/shared/ src/shared/
COPY src/gateway/ src/gateway/
COPY src/simulator/ src/simulator/
RUN dotnet publish src/gateway/AutoSphere.VehicleGateway/AutoSphere.VehicleGateway.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "AutoSphere.VehicleGateway.dll"]
