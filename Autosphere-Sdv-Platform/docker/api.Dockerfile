# syntax=docker/dockerfile:1
# AutoSphere backend API (ASP.NET Core + SignalR).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# Restore first so the dependency layer is cached independently of source changes.
COPY src/shared/AutoSphere.SharedKernel/AutoSphere.SharedKernel.csproj src/shared/AutoSphere.SharedKernel/
COPY src/shared/AutoSphere.Contracts/AutoSphere.Contracts.csproj src/shared/AutoSphere.Contracts/
COPY src/backend/AutoSphere.Domain/AutoSphere.Domain.csproj src/backend/AutoSphere.Domain/
COPY src/backend/AutoSphere.Application/AutoSphere.Application.csproj src/backend/AutoSphere.Application/
COPY src/backend/AutoSphere.Infrastructure/AutoSphere.Infrastructure.csproj src/backend/AutoSphere.Infrastructure/
COPY src/backend/AutoSphere.Api/AutoSphere.Api.csproj src/backend/AutoSphere.Api/
RUN dotnet restore src/backend/AutoSphere.Api/AutoSphere.Api.csproj
COPY src/shared/ src/shared/
COPY src/backend/ src/backend/
RUN dotnet publish src/backend/AutoSphere.Api/AutoSphere.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
# Volume mount points owned by the non-root user (Docker copies this ownership into new named volumes).
RUN mkdir -p /ota/private /ota/trust && chown -R "$APP_UID" /ota
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "AutoSphere.Api.dll"]
