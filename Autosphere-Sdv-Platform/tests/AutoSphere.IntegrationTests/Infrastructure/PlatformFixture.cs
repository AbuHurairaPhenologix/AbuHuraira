using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoSphere.Application.Users;
using AutoSphere.Application.Vehicles;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

[assembly: AssemblyFixture(typeof(AutoSphere.IntegrationTests.Infrastructure.PlatformFixture))]

namespace AutoSphere.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the complete platform in-process: embedded MQTT broker, the ASP.NET Core backend (SQLite,
/// in-memory cache) and the vehicle gateway hosting the simulated ECUs on an in-memory CAN bus.
/// No Docker, PostgreSQL or Redis is required, so the scenarios run in CI.
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    public const string VehicleId = "AUTO-001";
    public const string AdminPassword = "Admin#Test-Password1";
    public const string EngineerPassword = "Engineer#Test-Password1";
    public const string ViewerPassword = "Viewer#Test-Password1";
    private const string SecurityAccessSecret = "integration-test-security-access";

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "autosphere-it-" + Guid.NewGuid().ToString("N")[..8]);
    private MqttServer? _broker;
    private IHost? _gateway;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public ApiFactory Api { get; private set; } = null!;

    public int MqttPort { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workDirectory);
        MqttPort = FreePort();
        var serverFactory = new MqttServerFactory();
        _broker = serverFactory.CreateMqttServer(serverFactory.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(MqttPort).Build());
        await _broker.StartAsync();

        Api = new ApiFactory(_workDirectory, MqttPort);
        _ = Api.Server; // starts the backend: schema, seeding, signing key, MQTT connection

        var publicKeyPath = Path.Combine(_workDirectory, "ota-public.pem");
        var gateway = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing", DisableDefaults = true });
        gateway.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:VehicleId"] = VehicleId,
            ["Gateway:SecurityAccessSecret"] = SecurityAccessSecret,
            ["Gateway:OtaTrustedPublicKeyPath"] = publicKeyPath,
            ["Gateway:DtcPollIntervalMs"] = "1000",
            ["Gateway:StatusPublishIntervalMs"] = "2000",
            ["Mqtt:Host"] = "127.0.0.1",
            ["Mqtt:Port"] = MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CanBus:Transport"] = "InMemory",
            ["Simulation:Enabled"] = "true",
            ["Simulation:BootTimeMs"] = "400",
        });
        gateway.Logging.AddFilter(level => level >= LogLevel.Warning);
        gateway.Services.AddVehicleGateway(gateway.Configuration);
        _gateway = gateway.Build();
        await _gateway.StartAsync();

        using var client = await CreateClientAsync("admin", AdminPassword);
        await WaitUntilAsync(async () =>
        {
            var vehicle = await client.GetFromJsonAsync<VehicleDetailsDto>($"/api/vehicles/{VehicleId}", Json);
            return vehicle!.Connectivity == ConnectivityStatus.Online && vehicle.Health.Status == HealthStatus.Healthy
                   && vehicle.Ecus.Count(e => e.Status == EcuStatus.Online) >= 5;
        }, TimeSpan.FromSeconds(60), "vehicle did not become online and healthy");
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.StopAsync();
            _gateway.Dispose();
        }

        await Api.DisposeAsync();
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }

        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly on Windows; the temp folder is cleaned by the OS.
        }
    }

    public async Task<HttpClient> CreateClientAsync(string userName, string password)
    {
        var client = Api.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(userName, password));
        return client;
    }

    /// <summary>Logs in once per user and caches the token.</summary>
    public async Task<string> TokenAsync(string userName, string password)
    {
        if (_tokens.TryGetValue(userName, out var cached))
        {
            return cached;
        }

        using var client = Api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<AuthResult>(Json))!.AccessToken;
        _tokens[userName] = token;
        return token;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds} s: {because}");
            }

            await Task.Delay(500);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The backend under test with test-specific configuration.</summary>
    public sealed class ApiFactory(string workDirectory, int mqttPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:AutoSphere"] = $"Data Source={Path.Combine(workDirectory, "autosphere.db")}",
                ["ConnectionStrings:Redis"] = string.Empty,
                ["Mqtt:Host"] = "127.0.0.1",
                ["Mqtt:Port"] = mqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Jwt:SigningKey"] = "integration-tests-signing-key-with-more-than-32-bytes",
                ["Seed:Enabled"] = "true",
                ["Seed:AdminPassword"] = AdminPassword,
                ["Seed:EngineerPassword"] = EngineerPassword,
                ["Seed:ViewerPassword"] = ViewerPassword,
                ["Ota:Signing:PrivateKeyPath"] = Path.Combine(workDirectory, "ota-private.pem"),
                ["Ota:Signing:PublicKeyPath"] = Path.Combine(workDirectory, "ota-public.pem"),
                ["Ota:Signing:GenerateIfMissing"] = "true",
                ["Ota:HealthCheckSeconds"] = "4",
                ["FaultInjection:Enabled"] = "true",
                ["Swagger:Enabled"] = "true",
                ["RateLimiting:LoginPermitsPerMinute"] = "1000",
                ["Serilog:MinimumLevel:Default"] = "Warning",
            };
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
