using System.Threading.Channels;

namespace AutoSphere.CanBus.IsoTp;

public sealed record IsoTpOptions
{
    /// <summary>Padding byte for unused frame bytes (0xCC is a common choice in diagnostics).</summary>
    public byte Padding { get; init; } = 0xCC;

    /// <summary>Block size announced in our flow control frames (0 = send everything without further FC).</summary>
    public byte BlockSize { get; init; }

    /// <summary>STmin announced in our flow control frames.</summary>
    public byte SeparationTimeMin { get; init; }

    /// <summary>N_Bs: how long a sender waits for a flow control frame.</summary>
    public TimeSpan FlowControlTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>N_Cr: maximum gap between consecutive frames while receiving.</summary>
    public TimeSpan ConsecutiveFrameTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of FC.WAIT frames accepted before the transfer is aborted.</summary>
    public int MaxWaitFrames { get; init; } = 10;
}

public sealed class IsoTpException : Exception
{
    public IsoTpException()
    {
    }

    public IsoTpException(string message)
        : base(message)
    {
    }

    public IsoTpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A point-to-point ISO-TP style transport between two CAN ids (normal addressing). Messages up to
/// 4095 bytes are segmented into single/first/consecutive frames with flow control.
/// </summary>
public sealed class IsoTpChannel : IAsyncDisposable
{
    private readonly ICanChannel _can;
    private readonly IsoTpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<IsoTpFrameInfo> _flowControl = Channel.CreateUnbounded<IsoTpFrameInfo>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    // Reassembly state; only touched by the pump task.
    private byte[]? _rxBuffer;
    private int _rxReceived;
    private int _rxNextSequence;
    private int _rxBlockCounter;
    private DateTimeOffset _rxLastFrameAt;

    private IsoTpChannel(ICanChannel can, uint txId, uint rxId, IsoTpOptions options, TimeProvider timeProvider)
    {
        _can = can;
        TxId = txId;
        RxId = rxId;
        _options = options;
        _timeProvider = timeProvider;
        _pump = Task.Run(() => PumpAsync(_stopping.Token));
    }

    public uint TxId { get; }

    public uint RxId { get; }

    public static async Task<IsoTpChannel> OpenAsync(
        ICanBus bus, string name, uint txId, uint rxId, IsoTpOptions? options = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var can = await bus.OpenChannelAsync(name, [CanFilter.Exact(rxId)], cancellationToken);
        return new IsoTpChannel(can, txId, rxId, options ?? new IsoTpOptions(), timeProvider ?? TimeProvider.System);
    }

    /// <summary>Sends one message, segmenting it if necessary.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > IsoTpPci.MaxMessageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"ISO-TP messages must be 1..{IsoTpPci.MaxMessageLength} bytes.");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (payload.Length <= IsoTpPci.MaxSingleFramePayload)
            {
                await WriteAsync(IsoTpPci.SingleFrame(payload.Span, _options.Padding), cancellationToken);
                return;
            }

            while (_flowControl.Reader.TryRead(out _))
            {
                // discard stale flow control frames from an earlier, aborted transfer
            }

            await WriteAsync(IsoTpPci.FirstFrame(payload.Length, payload.Span, _options.Padding), cancellationToken);
            var offset = IsoTpPci.FirstFramePayload;
            var sequence = 1;

            while (offset < payload.Length)
            {
                var flow = await WaitForClearToSendAsync(cancellationToken);
                var separation = IsoTpPci.SeparationTime(flow.SeparationTimeMin);
                var framesInBlock = 0;

                while (offset < payload.Length && (flow.BlockSize == 0 || framesInBlock < flow.BlockSize))
                {
                    var chunkLength = Math.Min(IsoTpPci.ConsecutiveFramePayload, payload.Length - offset);
                    var consecutive = IsoTpPci.ConsecutiveFrame(sequence, payload.Span.Slice(offset, chunkLength), _options.Padding);
                    await WriteAsync(consecutive, cancellationToken);
                    offset += chunkLength;
                    sequence = (sequence + 1) & 0x0F;
                    framesInBlock++;
                    if (separation > TimeSpan.Zero && offset < payload.Length)
                    {
                        await Task.Delay(separation, _timeProvider, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Waits for the next complete message.</summary>
    public ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken) => _messages.Reader.ReadAsync(cancellationToken);

    /// <summary>Waits for the next complete message or returns <c>null</c> after <paramref name="timeout"/>.</summary>
    public async Task<byte[]?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _messages.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Drops already received but unread messages (e.g. late responses to a timed-out request).</summary>
    public void DiscardPendingMessages()
    {
        while (_messages.Reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
        }

        await _can.DisposeAsync();
        _messages.Writer.TryComplete();
        _stopping.Dispose();
        _sendLock.Dispose();
    }

    private async Task<IsoTpFrameInfo> WaitForClearToSendAsync(CancellationToken cancellationToken)
    {
        for (var waits = 0; ; waits++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.FlowControlTimeout);
            IsoTpFrameInfo flow;
            try
            {
                flow = await _flowControl.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IsoTpException($"N_Bs timeout: no flow control frame on 0x{RxId:X3} within {_options.FlowControlTimeout.TotalMilliseconds} ms.");
            }

            switch (flow.FlowStatus)
            {
                case IsoTpFlowStatus.ContinueToSend:
                    return flow;
                case IsoTpFlowStatus.Wait when waits < _options.MaxWaitFrames:
                    continue;
                case IsoTpFlowStatus.Wait:
                    throw new IsoTpException("Receiver sent too many FC.WAIT frames.");
                default:
                    throw new IsoTpException("Receiver reported buffer overflow (FC.OVFLW).");
            }
        }
    }

    private ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken) =>
        _can.WriteAsync(new CanFrame(TxId, data, _timeProvider.GetUtcNow()), cancellationToken);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _can.ReadAllAsync(cancellationToken))
            {
                await HandleFrameAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleFrameAsync(CanFrame frame, CancellationToken cancellationToken)
    {
        var data = frame.Data.Span;
        if (!IsoTpPci.TryParse(data, out var info))
        {
            return;
        }

        switch (info.Type)
        {
            case IsoTpFrameType.SingleFrame:
                _rxBuffer = null;
                _messages.Writer.TryWrite(data.Slice(info.DataOffset, info.DataLength).ToArray());
                break;

            case IsoTpFrameType.FirstFrame:
                _rxBuffer = new byte[info.MessageLength];
                data.Slice(info.DataOffset, info.DataLength).CopyTo(_rxBuffer);
                _rxReceived = info.DataLength;
                _rxNextSequence = 1;
                _rxBlockCounter = 0;
                _rxLastFrameAt = frame.Timestamp;
                await WriteAsync(IsoTpPci.FlowControl(IsoTpFlowStatus.ContinueToSend, _options.BlockSize, _options.SeparationTimeMin, _options.Padding), cancellationToken);
                break;

            case IsoTpFrameType.ConsecutiveFrame when _rxBuffer is not null:
                if (info.SequenceNumber != _rxNextSequence || frame.Timestamp - _rxLastFrameAt > _options.ConsecutiveFrameTimeout)
                {
                    _rxBuffer = null; // sequence error or N_Cr timeout: abort reception
                    break;
                }

                var count = Math.Min(info.DataLength, _rxBuffer.Length - _rxReceived);
                data.Slice(info.DataOffset, count).CopyTo(_rxBuffer.AsSpan(_rxReceived));
                _rxReceived += count;
                _rxNextSequence = (_rxNextSequence + 1) & 0x0F;
                _rxLastFrameAt = frame.Timestamp;

                if (_rxReceived >= _rxBuffer.Length)
                {
                    _messages.Writer.TryWrite(_rxBuffer);
                    _rxBuffer = null;
                }
                else if (_options.BlockSize > 0 && ++_rxBlockCounter >= _options.BlockSize)
                {
                    _rxBlockCounter = 0;
                    await WriteAsync(IsoTpPci.FlowControl(IsoTpFlowStatus.ContinueToSend, _options.BlockSize, _options.SeparationTimeMin, _options.Padding), cancellationToken);
                }

                break;

            case IsoTpFrameType.FlowControl:
                _flowControl.Writer.TryWrite(info);
                break;
        }
    }
}
