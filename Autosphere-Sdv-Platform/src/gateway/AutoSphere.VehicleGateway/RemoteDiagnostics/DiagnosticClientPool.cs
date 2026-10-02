using System.Collections.Concurrent;
using AutoSphere.CanBus;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Diagnostics.Addressing;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.VehicleGateway.RemoteDiagnostics;

/// <summary>
/// One UDS tester channel per ECU. Requests on a channel are serialized by <see cref="UdsClient"/>;
/// multi-step sequences that must not be interleaved (flashing) take the per-ECU <see cref="LockAsync"/>.
/// </summary>
public sealed class DiagnosticClientPool(ICanBus bus, TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<EcuType, Lazy<Task<UdsClient>>> _clients = new();
    private readonly ConcurrentDictionary<EcuType, SemaphoreSlim> _locks = new();

    public Task<UdsClient> GetAsync(EcuType ecuType) =>
        _clients.GetOrAdd(ecuType, type => new Lazy<Task<UdsClient>>(() => CreateAsync(type))).Value;

    /// <summary>Exclusive access to an ECU for a multi-request sequence.</summary>
    public async Task<IDisposable> LockAsync(EcuType ecuType, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(ecuType, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    /// <summary>Discards a client whose transport failed so that the next call creates a fresh channel.</summary>
    public async Task ResetAsync(EcuType ecuType)
    {
        if (_clients.TryRemove(ecuType, out var lazy) && lazy.IsValueCreated)
        {
            await (await lazy.Value).DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values.Where(l => l.IsValueCreated))
        {
            await (await lazy.Value).DisposeAsync();
        }

        _clients.Clear();
        foreach (var semaphore in _locks.Values)
        {
            semaphore.Dispose();
        }
    }

    private async Task<UdsClient> CreateAsync(EcuType ecuType)
    {
        var address = DiagnosticAddresses.For(ecuType);
        var channel = await IsoTpChannel.OpenAsync(bus, $"tester-{ecuType}", address.RequestId, address.ResponseId, timeProvider: timeProvider);
        return new UdsClient(channel);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
