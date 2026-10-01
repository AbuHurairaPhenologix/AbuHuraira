# ASP.NET Core backend (API + ingestion + background workers).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY AnomalyDetection.Domain/AnomalyDetection.Domain.csproj AnomalyDetection.Domain/
COPY AnomalyDetection.Application/AnomalyDetection.Application.csproj AnomalyDetection.Application/
COPY AnomalyDetection.Infrastructure/AnomalyDetection.Infrastructure.csproj AnomalyDetection.Infrastructure/
COPY AnomalyDetection.Api/AnomalyDetection.Api.csproj AnomalyDetection.Api/
RUN dotnet restore AnomalyDetection.Api/AnomalyDetection.Api.csproj
COPY AnomalyDetection.Domain/ AnomalyDetection.Domain/
COPY AnomalyDetection.Application/ AnomalyDetection.Application/
COPY AnomalyDetection.Infrastructure/ AnomalyDetection.Infrastructure/
COPY AnomalyDetection.Api/ AnomalyDetection.Api/
RUN dotnet publish AnomalyDetection.Api/AnomalyDetection.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 CMD curl -fs http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "AnomalyDetection.Api.dll"]
