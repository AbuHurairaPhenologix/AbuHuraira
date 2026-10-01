using System.Collections.Concurrent;
using System.Globalization;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Ingestion;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Api.Demo;

/// <summary>Named anomaly scenarios that can be switched on explicitly in development/demo mode only.</summary>
public static class DemoScenarios
{
    public const string LatencySpike = "latency_spike";
    public const string ErrorBurst = "error_burst";
    public const string AuthFailureBurst = "auth_failure_burst";
    public const string DependencyFailure = "dependency_failure";
    public const string RetryStorm = "retry_storm";
    public const string TrafficSurge = "traffic_surge";

    public static readonly IReadOnlyList<string> All = [LatencySpike, ErrorBurst, AuthFailureBurst, DependencyFailure, RetryStorm, TrafficSurge];
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Allows toggling anomaly scenarios. Forced off outside Development unless explicitly enabled.</summary>
    public bool EnableScenarios { get; set; }

    /// <summary>Normal-mode latency: log-normal with this mean (ms); p95 ≈ 2.6 × mean like the benchmark baseline.</summary>
    public double MeanLatencyMs { get; set; } = 250;

    public double LatencySigma { get; set; } = 0.754;

    public double NormalErrorProbability { get; set; } = 0.02;

    public double NormalLoginFailureProbability { get; set; } = 0.10;

    public double NormalDependencyFailureProbability { get; set; } = 0.007;

    public double NormalJobRetryProbability { get; set; } = 0.005;
}

public sealed record ActiveScenario(string Name, DateTime ActivatedAtUtc, DateTime ExpiresAtUtc);

/// <summary>Thread-safe scenario switchboard. Normal mode generates normal telemetry; scenarios expire automatically.</summary>
public sealed class DemoScenarioState(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, ActiveScenario> _active = new(StringComparer.Ordinal);

    public ActiveScenario Activate(string name, TimeSpan duration)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var scenario = new ActiveScenario(name, now, now + duration);
        _active[name] = scenario;
        return scenario;
    }

    public bool Deactivate(string name) => _active.TryRemove(name, out _);

    public void Clear() => _active.Clear();

    public bool IsActive(string name)
    {
        if (!_active.TryGetValue(name, out var s))
        {
            return false;
        }

        if (s.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
        {
            return true;
        }

        _active.TryRemove(name, out _);
        return false;
    }

    public IReadOnlyList<ActiveScenario> Snapshot() => _active.Values.Where(s => IsActive(s.Name)).OrderBy(s => s.Name).ToList();
}

/// <summary>
/// The simulated "demo shop" workload whose telemetry is analysed. Latency is real (the request actually waits);
/// dependency and background-job outcomes are emitted as structured events through the same queue as the middleware.
/// </summary>
public sealed class DemoWorkload(
    DemoScenarioState scenarios,
    IEventQueue queue,
    IOptions<DemoOptions> demoOptions,
    IOptions<PipelineOptions> pipelineOptions)
{
    private DemoOptions O => demoOptions.Value;

    public TimeSpan SampleLatency(double multiplier = 1.0)
    {
        // Log-normal with requested mean: mu = ln(mean) − σ²/2.
        var sigma = O.LatencySigma;
        var mu = Math.Log(O.MeanLatencyMs) - (sigma * sigma / 2);
        var normal = Math.Sqrt(-2 * Math.Log(1 - Random.Shared.NextDouble())) * Math.Cos(2 * Math.PI * Random.Shared.NextDouble());
        var ms = Math.Exp(mu + (sigma * normal));
        if (scenarios.IsActive(DemoScenarios.LatencySpike))
        {
            ms *= 3.2;
        }

        return TimeSpan.FromMilliseconds(Math.Min(ms * multiplier, 8_000));
    }

    public bool ShouldFail() =>
        Random.Shared.NextDouble() < (scenarios.IsActive(DemoScenarios.ErrorBurst) ? 0.13 : O.NormalErrorProbability);

    public bool LoginFails(string? password) =>
        string.Equals(password, "wrong", StringComparison.Ordinal)
        || Random.Shared.NextDouble() < (scenarios.IsActive(DemoScenarios.AuthFailureBurst) ? 0.75 : O.NormalLoginFailureProbability);

    /// <summary>Simulates a downstream dependency call with retries; emits a dependency_call event.</summary>
    public async Task<(bool Succeeded, int Retries)> CallDependencyAsync(string dependency, string correlationId, CancellationToken ct)
    {
        var unstable = scenarios.IsActive(DemoScenarios.DependencyFailure);
        var failureProbability = unstable ? 0.35 : O.NormalDependencyFailureProbability;
        var retries = 0;
        var started = DateTime.UtcNow;
        var succeeded = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(SampleLatency(0.4), ct);
            if (Random.Shared.NextDouble() >= failureProbability)
            {
                succeeded = true;
                break;
            }

            if (attempt < 2)
            {
                retries++;
            }
        }

        Emit("dependency_call", "/dependency/" + dependency, succeeded ? 200 : 503, (DateTime.UtcNow - started).TotalMilliseconds, !succeeded, dependency, unstable ? retries : 0, correlationId);
        return (succeeded, retries);
    }

    /// <summary>Simulates a background job execution; emits a background_job event with its retry count.</summary>
    public async Task<int> RunJobAsync(string correlationId, CancellationToken ct)
    {
        var storm = scenarios.IsActive(DemoScenarios.RetryStorm);
        var retries = storm ? Random.Shared.Next(2, 7) : (Random.Shared.NextDouble() < O.NormalJobRetryProbability ? 1 : 0);
        var started = DateTime.UtcNow;
        await Task.Delay(SampleLatency(0.5), ct);
        Emit("background_job", "/jobs/reconcile-inventory", 200, (DateTime.UtcNow - started).TotalMilliseconds, false, null, retries, correlationId);
        return retries;
    }

    public void EmitAuthentication(bool success, string correlationId) =>
        Emit("authentication", "/auth/login", success ? 200 : 401, null, false, null, 0, correlationId, success ? "success" : "failure");

    private void Emit(string type, string endpoint, int status, double? durationMs, bool error, string? dependency, int retries, string correlationId, string? auth = null)
    {
        queue.TryEnqueue(new RawEventInput
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventTimestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ServiceName = pipelineOptions.Value.DemoServiceName,
            Environment = pipelineOptions.Value.DemoEnvironment,
            EventType = type,
            EndpointGroup = endpoint,
            StatusCode = status.ToString(CultureInfo.InvariantCulture),
            DurationMs = durationMs is null ? null : RawEventInput.FormatNumber(Math.Round(durationMs.Value, 3)),
            ErrorFlag = error ? "true" : "false",
            AuthenticationResult = auth,
            DependencyName = dependency,
            RetryCount = retries.ToString(CultureInfo.InvariantCulture),
            CorrelationId = correlationId,
        });
    }
}
