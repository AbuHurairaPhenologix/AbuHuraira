using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Connections;
using ThermoTwin.Application;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Tests.Application;

namespace ThermoTwin.Tests.Integration;

public sealed class ThermoTwinApiFactory : WebApplicationFactory<Program>
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"thermotwin-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ThermoTwin", $"Data Source={_database}");
        builder.UseSetting("ThermoTwin:AutoStartDemo", "false");
        builder.UseSetting("ThermoTwin:SeedExperiments", "false");
    }
}

public sealed class ApiIntegrationTests : IClassFixture<ThermoTwinApiFactory>
{
    private readonly ThermoTwinApiFactory _factory;

    public ApiIntegrationTests(ThermoTwinApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_reports_healthy_database()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Scenario_catalogue_is_served_with_string_enums()
    {
        var client = _factory.CreateClient();
        var json = await client.GetStringAsync(new Uri("/api/scenarios", UriKind.Relative));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(4, doc.RootElement.GetArrayLength());
        Assert.Equal("CrankNicolson", doc.RootElement[0].GetProperty("solver").GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task Invalid_scenario_returns_problem_details_with_field_errors()
    {
        var client = _factory.CreateClient();
        var scenario = ScenarioCatalog.RapidChargeHiddenHotspot with { Solver = new SolverSettings(TimeScheme.ExplicitEuler, 5, 1800) };
        var response = await client.PostAsJsonAsync(new Uri("/api/simulations", UriKind.Relative), new { scenario }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Validation failed", doc.RootElement.GetProperty("title").GetString());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("Solver.TimeStep", out _));
    }

    [Fact]
    public async Task Unknown_experiment_kind_and_missing_results_are_handled()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(new Uri("/api/experiments/NotAKind/run", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/api/experiments/latest/CoolingComparison", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Stability_endpoint_flags_unstable_explicit_scheme()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(new Uri("/api/numerics/stability", UriKind.Relative), ScenarioCatalog.RapidChargeHiddenHotspot, JsonDefaults.Options);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemes = doc.RootElement.GetProperty("grids")[0].GetProperty("schemes");
        var explicitEuler = schemes.EnumerateArray().First(s => s.GetProperty("scheme").GetString() == "ExplicitEuler");
        var crankNicolson = schemes.EnumerateArray().First(s => s.GetProperty("scheme").GetString() == "CrankNicolson");
        Assert.False(explicitEuler.GetProperty("isStable").GetBoolean());
        Assert.True(crankNicolson.GetProperty("isStable").GetBoolean());
    }

    [Fact]
    public async Task Starting_a_simulation_streams_frames_over_signalr_and_persists_the_run()
    {
        var client = _factory.CreateClient();
        var server = _factory.Server;

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/twin"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => JsonDefaults.Apply(o.PayloadSerializerOptions))
            .Build();

        var frames = new List<TwinFrame>();
        var completed = new TaskCompletionSource<TwinFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TwinFrame>("twinFrame", frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }

            if (frame.Status == Domain.Enums.SimulationStatus.Completed)
            {
                completed.TrySetResult(frame);
            }
        });
        await connection.StartAsync();

        var response = await client.PostAsJsonAsync(new Uri("/api/simulations", UriKind.Relative), new { scenario = TestScenarios.Quick }, JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var runId = created.RootElement.GetProperty("id").GetGuid();

        var final = await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await connection.DisposeAsync();

        Assert.Equal(runId, final.RunId);
        Assert.True(frames.Count >= 2);
        Assert.NotNull(final.Estimation);
        Assert.Equal(final.TotalSteps, final.Step);

        var history = await client.GetFromJsonAsync<List<TwinHistoryPoint>>(new Uri("/api/twin/history", UriKind.Relative), JsonDefaults.Options);
        Assert.Equal(final.TotalSteps, history!.Count);

        using var run = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/simulations/{runId}", UriKind.Relative)));
        Assert.Equal("Completed", run.RootElement.GetProperty("status").GetString());
        Assert.True(run.RootElement.GetProperty("peakTemperature").GetDouble() > 25);

        using var snapshots = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/simulations/{runId}/snapshots", UriKind.Relative)));
        Assert.True(snapshots.RootElement.GetArrayLength() > 0);

        // The session is complete, so pausing is a state conflict (409).
        var pause = await client.PostAsync(new Uri("/api/twin/pause", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Conflict, pause.StatusCode);
    }
}
