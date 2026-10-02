namespace AutoSphere.CanBus;

/// <summary>Reads frames received on a CAN channel.</summary>
public interface ICanFrameReader
{
    /// <summary>Asynchronously enumerates received frames until cancelled or the channel is closed.</summary>
    IAsyncEnumerable<CanFrame> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the next received frame.</summary>
    ValueTask<CanFrame> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Transmits frames on a CAN channel.</summary>
public interface ICanFrameWriter
{
    ValueTask WriteAsync(CanFrame frame, CancellationToken cancellationToken);
}

/// <summary>
/// An open endpoint on a CAN bus, equivalent to a SocketCAN raw socket: it receives every frame that
/// matches its filters and that was <b>not</b> written by itself.
/// </summary>
public interface ICanChannel : ICanFrameReader, ICanFrameWriter, IAsyncDisposable
{
    string Name { get; }

    CanChannelStatistics Statistics { get; }
}

/// <summary>A CAN network (physical, virtual or simulated) on which channels can be opened.</summary>
public interface ICanBus : IAsyncDisposable
{
    /// <summary>Human readable transport description, e.g. <c>socketcan:vcan0</c>.</summary>
    string Description { get; }

    ValueTask<ICanChannel> OpenChannelAsync(string name, IReadOnlyList<CanFilter>? filters = null, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe counters for one channel.</summary>
public sealed class CanChannelStatistics
{
    private long _framesReceived;
    private long _framesSent;
    private long _framesDropped;

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>Frames lost because the receive queue was full (receive buffer overrun).</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public void OnReceived() => Interlocked.Increment(ref _framesReceived);

    public void OnSent() => Interlocked.Increment(ref _framesSent);

    public void OnDropped() => Interlocked.Increment(ref _framesDropped);
}

public class CanBusException : Exception
{
    public CanBusException()
    {
    }

    public CanBusException(string message)
        : base(message)
    {
    }

    public CanBusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
