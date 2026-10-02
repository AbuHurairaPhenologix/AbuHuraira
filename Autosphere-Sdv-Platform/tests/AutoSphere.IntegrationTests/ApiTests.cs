using System.Net;
using System.Net.Http.Json;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.IntegrationTests.Infrastructure;
using AutoSphere.SharedKernel.Signals;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSphere.IntegrationTests;

public sealed class ApiTests(PlatformFixture platform)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        using var anonymous = platform.Api.CreateClient();

        var response = await anonymous.GetAsync("/api/vehicles", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_credentials_are_rejected_with_problem_details()
    {
        using var anonymous = platform.Api.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "wrong-password" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("viewer", PlatformFixture.ViewerPassword, "/api/vehicles/AUTO-001/diagnostics/scan", HttpStatusCode.Forbidden)]
    [InlineData("viewer", PlatformFixture.ViewerPassword, "/api/vehicles/AUTO-001/faults", HttpStatusCode.Forbidden)]
    [InlineData("engineer", PlatformFixture.EngineerPassword, "/api/vehicles/AUTO-001/faults", HttpStatusCode.Forbidden)]
    [InlineData("engineer", PlatformFixture.EngineerPassword, "/api/ota/campaigns", HttpStatusCode.Forbidden)]
    public async Task Role_based_authorization_is_enforced(string user, string password, string path, HttpStatusCode expected)
    {
        using var client = await platform.CreateClientAsync(user, password);

        var response = await client.PostAsJsonAsync(path, new { }, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Every_role_can_read_the_simulation_status()
    {
        using var viewer = await platform.CreateClientAsync("viewer", PlatformFixture.ViewerPassword);

        var response = await viewer.GetAsync("/api/simulation/status", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_vehicle_with_ecus_and_versions()
    {
        using var client = await platform.CreateClientAsync("viewer", PlatformFixture.ViewerPassword);

        var vehicle = await client.GetFromJsonAsync<VehicleDetailsDto>("/api/vehicles/AUTO-001", PlatformFixture.Json, Ct);

        Assert.Equal("AutoSphere EV Prototype", vehicle!.Model);
        Assert.Contains(vehicle.Ecus, e => e.EcuId == "BMS-001" && !string.IsNullOrEmpty(e.SoftwareVersion));
        Assert.Equal(5, vehicle.Ecus.Count);
    }

    [Fact]
    public async Task Invalid_registration_returns_validation_problem()
    {
        using var client = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);

        var response = await client.PostAsJsonAsync("/api/vehicles", new RegisterVehicleRequest("bad id!", "SHORTVIN", "", 1800, null), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("VehicleId", problem!.Errors.Keys);
        Assert.Contains("Vin", problem.Errors.Keys);
    }

    [Fact]
    public async Task Vehicle_can_be_registered_and_deleted_by_administrator()
    {
        using var client = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);

        var created = await client.PostAsJsonAsync("/api/vehicles",
            new RegisterVehicleRequest("TEST-777", "WASPH1EV2T0000777", "Test Vehicle", 2026, []), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/vehicles",
            new RegisterVehicleRequest("TEST-777", "WASPH1EV2T0000778", "Test Vehicle", 2026, []), Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/vehicles/TEST-777", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/vehicles/TEST-777", Ct)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_reports_dependencies_without_authentication()
    {
        using var anonymous = platform.Api.CreateClient();

        var response = await anonymous.GetStringAsync("/health", Ct);

        Assert.Contains("\"mqtt\"", response, StringComparison.Ordinal);
        Assert.Contains("\"database\"", response, StringComparison.Ordinal);
        Assert.Contains("\"vehicle-gateways\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swagger_document_is_served()
    {
        using var anonymous = platform.Api.CreateClient();

        var document = await anonymous.GetStringAsync("/swagger/v1/swagger.json", Ct);

        Assert.Contains("/api/vehicles/{vehicleId}/dtcs/clear", document, StringComparison.Ordinal);
    }
}

public sealed class TelemetryTests(PlatformFixture platform)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Latest_telemetry_contains_plausible_decoded_values()
    {
        using var client = await platform.CreateClientAsync("viewer", PlatformFixture.ViewerPassword);

        var telemetry = await client.GetFromJsonAsync<VehicleTelemetryDto>("/api/vehicles/AUTO-001/telemetry/latest", PlatformFixture.Json, Ct);

        var snapshot = telemetry!.Snapshot;
        Assert.InRange(snapshot.SpeedKmh!.Value, 40, 110);
        Assert.InRange(snapshot.MotorRpm!.Value, 1500, 6500);
        Assert.InRange(snapshot.BatteryStateOfChargePercent!.Value, 70, 80);
        Assert.InRange(snapshot.BatteryTemperatureC!.Value, 25, 50);
        Assert.Equal(GearPositionName.Drive, snapshot.Gear?.ToString());
        Assert.Contains(telemetry.Signals, s => s.Path == VssPaths.BatteryStateOfCharge && s.Unit == "%");
        Assert.True(telemetry.Latency.CanToBackendMs >= 0);
    }

    [Fact]
    public async Task Live_telemetry_is_pushed_through_signalr()
    {
        var token = await platform.TokenAsync("viewer", PlatformFixture.ViewerPassword);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(platform.Api.Server.BaseAddress, "/hubs/vehicles"), options =>
            {
                options.HttpMessageHandlerFactory = _ => platform.Api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
            .Build();
        var received = new TaskCompletionSource<VehicleTelemetryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<VehicleTelemetryDto>("Telemetry", t => received.TrySetResult(t));

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("SubscribeVehicle", PlatformFixture.VehicleId, Ct);
        var telemetry = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(PlatformFixture.VehicleId, telemetry.VehicleId);
        Assert.NotNull(telemetry.Snapshot.SpeedKmh);
    }

    [Fact]
    public async Task Sampled_telemetry_is_persisted_and_queryable()
    {
        using var client = await platform.CreateClientAsync("viewer", PlatformFixture.ViewerPassword);
        var path = $"/api/vehicles/AUTO-001/telemetry/history?signal={VssPaths.VehicleSpeed}&signal={VssPaths.BatteryTemperature}&maxPoints=50";

        IReadOnlyList<TelemetrySeriesDto>? series = null;
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            series = await client.GetFromJsonAsync<IReadOnlyList<TelemetrySeriesDto>>(path, PlatformFixture.Json, Ct);
            return series!.All(s => s.Points.Count >= 3);
        }, TimeSpan.FromSeconds(20), "telemetry samples were not persisted");

        Assert.Equal(2, series!.Count);
        Assert.True(series[0].Points.Zip(series[0].Points.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
    }

    private static class GearPositionName
    {
        public const string Drive = "Drive";
    }
}
