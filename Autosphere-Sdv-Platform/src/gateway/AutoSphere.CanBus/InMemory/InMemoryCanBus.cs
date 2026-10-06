using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AutoSphere.CanBus.InMemory;

/// <summary>
/// A simulated CAN bus inside one process. It reproduces the SocketCAN delivery semantics that matter
/// to the application: broadcast to every other open channel, per-channel acceptance filters,
/// no echo to the sender and a bounded receive queue that drops the oldest frame on overrun.
/// </summary>
/// <remarks>
/// Arbitration, bit timing and error frames are not simulated; frames are delivered in write order.
/// </remarks>
public sealed class InMemoryCanBus : ICanBus
{
    private readonly ConcurrentDictionary<Guid, InMemoryCanChannel> _channels = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _receiveQueueCapacity;
    private volatile bool _disposed;

    public InMemoryCanBus(TimeProvider? timeProvider = null, int receiveQueueCapacity = 8192, string description = "in-memory")
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _receiveQueueCapacity = receiveQueueCapacity;
        Description = description;
    }

    public string Description { get; }

    /// <summary>Total number of frames written on the bus (useful for bus load estimation).</summary>
    public long FramesTransmitted => Interlocked.Read(ref _framesTransmitted);

    private long _framesTransmitted;

    public ValueTask<ICanChannel> OpenChannelAsync(string name, IReadOnlyList<CanFilter>? filters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = new InMemoryCanChannel(this, name, filters?.ToArray(), _receiveQueueCapacity);
        _channels[channel.Key] = channel;
        return ValueTask.FromResult<ICanChannel>(channel);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var channel in _channels.Values)
        {
            channel.Complete();
        }

        _channels.Clear();
        return ValueTask.CompletedTask;
    }

    private void Transmit(InMemoryCanChannel sender, CanFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stamped = frame with { Timestamp = _timeProvider.GetUtcNow(), Source = frame.Source ?? sender.Name };
        Interlocked.Increment(ref _framesTransmitted);
        foreach (var channel in _channels.Values)
        {
            if (!ReferenceEquals(channel, sender))
            {
                channel.Deliver(stamped);
            }
        }
    }

    private void Remove(InMemoryCanChannel channel) => _channels.TryRemove(channel.Key, out _);

    private sealed class InMemoryCanChannel : ICanChannel
    {
        private readonly InMemoryCanBus _bus;
        private readonly CanFilter[]? _filters;
        private readonly Channel<CanFrame> _queue;

        public InMemoryCanChannel(InMemoryCanBus bus, string name, CanFilter[]? filters, int capacity)
        {
            _bus = bus;
            _filters = filters;
            Name = name;
            _queue = Channel.CreateBounded<CanFrame>(
                new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false },
                _ => Statistics.OnDropped());
        }

        public Guid Key { get; } = Guid.NewGuid();

        public string Name { get; }

        public CanChannelStatistics Statistics { get; } = new();

        public void Deliver(CanFrame frame)
        {
            if (CanFilter.MatchesAny(_filters, frame) && _queue.Writer.TryWrite(frame))
            {
                Statistics.OnReceived();
            }
        }

        public void Complete() => _queue.Writer.TryComplete();

        public ValueTask WriteAsync(CanFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _bus.Transmit(this, frame);
            Statistics.OnSent();
            return ValueTask.CompletedTask;
        }

        public ValueTask<CanFrame> ReadAsync(CancellationToken cancellationToken) => _queue.Reader.ReadAsync(cancellationToken);

        public async IAsyncEnumerable<CanFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            _bus.Remove(this);
            Complete();
            return ValueTask.CompletedTask;
        }
    }
}
