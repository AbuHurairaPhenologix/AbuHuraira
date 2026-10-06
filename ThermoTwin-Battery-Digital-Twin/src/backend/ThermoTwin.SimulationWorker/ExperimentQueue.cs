using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.SimulationWorker;

public enum ExperimentJobState
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed record ExperimentJobStatus(ExperimentKind Kind, ExperimentJobState State, DateTimeOffset UpdatedAt, string? Error);

/// <summary>Unbounded FIFO of experiment requests with per-kind status tracking.</summary>
public sealed class ExperimentQueue
{
    private readonly Channel<ExperimentKind> _channel = Channel.CreateUnbounded<ExperimentKind>();
    private readonly ConcurrentDictionary<ExperimentKind, ExperimentJobStatus> _status = new();
    private readonly TimeProvider _clock;

    public ExperimentQueue(TimeProvider clock) => _clock = clock;

    public IReadOnlyList<ExperimentJobStatus> Status => _status.Values.OrderBy(s => s.Kind).ToArray();

    /// <summary>Queues an experiment unless the same kind is already queued or running.</summary>
    public bool Enqueue(ExperimentKind kind)
    {
        if (_status.TryGetValue(kind, out var current) && current.State is ExperimentJobState.Queued or ExperimentJobState.Running)
        {
            return false;
        }

        _status[kind] = new ExperimentJobStatus(kind, ExperimentJobState.Queued, _clock.GetUtcNow(), null);
        return _channel.Writer.TryWrite(kind);
    }

    internal ChannelReader<ExperimentKind> Reader => _channel.Reader;

    internal void Mark(ExperimentKind kind, ExperimentJobState state, string? error = null) =>
        _status[kind] = new ExperimentJobStatus(kind, state, _clock.GetUtcNow(), error);
}

/// <summary>Consumes the experiment queue one job at a time on a background thread.</summary>
public sealed class ExperimentWorker : BackgroundService
{
    private readonly ExperimentQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITwinNotifier _notifier;
    private readonly ILogger<ExperimentWorker> _logger;

    public ExperimentWorker(ExperimentQueue queue, IServiceScopeFactory scopeFactory, ITwinNotifier notifier, ILogger<ExperimentWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var kind in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _queue.Mark(kind, ExperimentJobState.Running);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ExperimentService>();
                var record = await service.RunAsync(kind, null, stoppingToken);
                _queue.Mark(kind, ExperimentJobState.Completed);
                await _notifier.PublishExperimentAsync(
                    new ExperimentSummaryDto(record.Id, record.Kind, ExperimentRunner.Describe(record.Kind), record.Summary, record.CreatedAt, record.DurationMs),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Experiment {Kind} failed", kind);
                _queue.Mark(kind, ExperimentJobState.Failed, ex.Message);
            }
        }
    }
}
