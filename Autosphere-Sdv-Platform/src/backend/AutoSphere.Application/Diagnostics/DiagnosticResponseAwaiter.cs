using System.Collections.Concurrent;

namespace AutoSphere.Application.Diagnostics;

/// <summary>
/// Bridges the synchronous HTTP request with the asynchronous MQTT response: the HTTP side registers
/// the correlation id before publishing and awaits completion; the MQTT ingestion completes it.
/// </summary>
public sealed class DiagnosticResponseAwaiter
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DiagnosticSessionDto>> _pending = new();

    public Task<DiagnosticSessionDto> Register(Guid correlationId)
    {
        var completion = new TaskCompletionSource<DiagnosticSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = completion;
        return completion.Task;
    }

    public bool TryComplete(Guid correlationId, DiagnosticSessionDto session) =>
        _pending.TryRemove(correlationId, out var completion) && completion.TrySetResult(session);

    public void Abandon(Guid correlationId) => _pending.TryRemove(correlationId, out _);

    public int PendingCount => _pending.Count;
}
