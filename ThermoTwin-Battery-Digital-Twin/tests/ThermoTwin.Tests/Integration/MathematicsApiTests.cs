using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ThermoTwin.Application;

namespace ThermoTwin.Tests.Integration;

[Collection("Api")]
public sealed class MathematicsApiTests
{
    private readonly ThermoTwinApiFactory _factory;

    public MathematicsApiTests(ThermoTwinApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Fem_mesh_endpoint_returns_a_triangulation()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/numerics/fem/mesh?nx=4&ny=2", UriKind.Relative)));
        Assert.Equal(15, doc.RootElement.GetProperty("nodes").GetInt32());
        Assert.Equal(16, doc.RootElement.GetProperty("triangles").GetArrayLength());
        Assert.Equal(3, doc.RootElement.GetProperty("triangles")[0].GetArrayLength());
    }

    [Fact]
    public async Task Fem_mesh_endpoint_rejects_invalid_resolution()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/numerics/fem/mesh?nx=0&ny=500", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Nx", out _));
        Assert.True(errors.TryGetProperty("Ny", out _));
    }

    [Fact]
    public async Task Gradient_check_endpoint_confirms_the_adjoint()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(new Uri("/api/numerics/adjoint/gradient-check", UriKind.Relative),
            new { segments = 3, segmentDuration = 100, mu = 1e4, epsilon = 1e-6, seed = 2 }, JsonDefaults.Options);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("relativeError").GetDouble() < 1e-4);
        Assert.Equal(3, doc.RootElement.GetProperty("adjointGradient").GetArrayLength());
        Assert.Equal(6, doc.RootElement.GetProperty("finiteDifferenceSolves").GetInt32());
    }

    [Fact]
    public async Task Gradient_check_endpoint_validates_the_horizon()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(new Uri("/api/numerics/adjoint/gradient-check", UriKind.Relative),
            new { segments = 3, segmentDuration = 15 }, JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("SegmentDuration", out _));
    }

    [Fact]
    public async Task New_experiment_kinds_are_queued_executed_and_served()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(new Uri("/api/experiments/latest/ParameterIdentifiability", UriKind.Relative))).StatusCode);

        var run = await client.PostAsync(new Uri("/api/experiments/ParameterIdentifiability/run", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var duplicate = await client.PostAsync(new Uri("/api/experiments/ParameterIdentifiability/run", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        JsonDocument? detail = null;
        for (var attempt = 0; attempt < 240 && detail is null; attempt++)
        {
            var response = await client.GetAsync(new Uri("/api/experiments/latest/ParameterIdentifiability", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
            else
            {
                await Task.Delay(500);
            }
        }

        Assert.NotNull(detail);
        using (detail)
        {
            Assert.Equal("ParameterIdentifiability", detail.RootElement.GetProperty("kind").GetString());
            Assert.Equal(5, detail.RootElement.GetProperty("result").GetProperty("report").GetProperty("parameters").GetArrayLength());
        }
    }

    [Fact]
    public async Task Scenario_contract_exposes_the_optimiser_choice()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/scenarios", UriKind.Relative)));
        var control = doc.RootElement[0].GetProperty("control");
        Assert.Equal("AdjointFullOrder", control.GetProperty("optimizer").GetString());
        Assert.Equal(40, control.GetProperty("romModes").GetInt32());
    }
}
