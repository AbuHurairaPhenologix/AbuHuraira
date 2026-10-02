using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace AutoSphere.CanBus.SocketCan;

/// <summary>
/// CAN transport for Linux SocketCAN interfaces (<c>vcan0</c>, <c>can0</c>, …). Each channel is an
/// independent <c>CAN_RAW</c> socket bound to the interface, so the kernel provides filtering and
/// local loopback exactly as for any other SocketCAN application (candump, cansend, …).
/// </summary>
/// <remarks>
/// Receive timestamps are taken in user space after <c>read(2)</c> returns; kernel timestamps
/// (<c>SO_TIMESTAMP</c>) are not used. Set up a virtual bus with <c>scripts/setup-vcan.sh</c>.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class SocketCanBus : ICanBus
{
    private readonly string _interfaceName;
    private readonly bool _enableFd;
    private readonly TimeProvider _timeProvider;

    public SocketCanBus(string interfaceName, bool enableFd = false, TimeProvider? timeProvider = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("SocketCAN is only available on Linux. Use the InMemory or Udp transport instead.");
        }

        _interfaceName = interfaceName;
        _enableFd = enableFd;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Description => $"socketcan:{_interfaceName}";

    public ValueTask<ICanChannel> OpenChannelAsync(string name, IReadOnlyList<CanFilter>? filters = null, CancellationToken cancellationToken = default)
    {
        var fd = OpenSocket(filters);
        return ValueTask.FromResult<ICanChannel>(new SocketCanChannel(fd, name, _timeProvider));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private unsafe int OpenSocket(IReadOnlyList<CanFilter>? filters)
    {
        var ifIndex = SocketCanNative.if_nametoindex(_interfaceName);
        if (ifIndex == 0)
        {
            throw new CanBusException($"CAN interface '{_interfaceName}' was not found (errno {Marshal.GetLastPInvokeError()}). Did you run scripts/setup-vcan.sh?");
        }

        var fd = SocketCanNative.socket(SocketCanNative.PfCan, SocketCanNative.SockRaw, SocketCanNative.CanRaw);
        if (fd < 0)
        {
            throw new CanBusException($"socket(PF_CAN) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (_enableFd)
            {
                var enable = 1;
                Check(SocketCanNative.setsockopt(fd, SocketCanNative.SolCanRaw, SocketCanNative.CanRawFdFrames, &enable, sizeof(int)), "CAN_RAW_FD_FRAMES");
            }

            if (filters is { Count: > 0 })
            {
                var encoded = SocketCanFrameCodec.EncodeFilters(filters);
                fixed (byte* pFilters = encoded)
                {
                    Check(SocketCanNative.setsockopt(fd, SocketCanNative.SolCanRaw, SocketCanNative.CanRawFilter, pFilters, (uint)encoded.Length), "CAN_RAW_FILTER");
                }
            }

            // struct sockaddr_can { sa_family_t can_family; int can_ifindex; union { ... } can_addr; }
            var address = stackalloc byte[SocketCanNative.SockAddrCanSize];
            new Span<byte>(address, SocketCanNative.SockAddrCanSize).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(address, 2), SocketCanNative.PfCan);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(address + 4, 4), (int)ifIndex);
            Check(SocketCanNative.bind(fd, address, SocketCanNative.SockAddrCanSize), "bind");
            return fd;
        }
        catch
        {
            SocketCanNative.close(fd);
            throw;
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new CanBusException($"{operation} failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }

    private sealed class SocketCanChannel : ICanChannel
    {
        private readonly int _fd;
        private readonly TimeProvider _timeProvider;
        private readonly Channel<CanFrame> _received = Channel.CreateBounded<CanFrame>(
            new BoundedChannelOptions(8192) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly CancellationTokenSource _stopping = new();
        private readonly Thread _readerThread;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private volatile bool _closed;

        public SocketCanChannel(int fd, string name, TimeProvider timeProvider)
        {
            _fd = fd;
            _timeProvider = timeProvider;
            Name = name;
            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"socketcan-{name}" };
            _readerThread.Start();
        }

        public string Name { get; }

        public CanChannelStatistics Statistics { get; } = new();

        public async ValueTask WriteAsync(CanFrame frame, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var buffer = new byte[SocketCanFrameCodec.CanFdMtu];
            var length = SocketCanFrameCodec.Encode(frame, buffer);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                WriteRaw(buffer, length);
                Statistics.OnSent();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public ValueTask<CanFrame> ReadAsync(CancellationToken cancellationToken) => _received.Reader.ReadAsync(cancellationToken);

        public async IAsyncEnumerable<CanFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _received.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            await _stopping.CancelAsync();
            _readerThread.Join(TimeSpan.FromSeconds(1));
            SocketCanNative.close(_fd);
            _received.Writer.TryComplete();
            _stopping.Dispose();
            _writeLock.Dispose();
        }

        private unsafe void WriteRaw(byte[] buffer, int length)
        {
            fixed (byte* pBuffer = buffer)
            {
                var written = SocketCanNative.write(_fd, pBuffer, length);
                if (written != length)
                {
                    // ENOBUFS (105) means the interface TX queue is full: the frame is lost like on a real bus.
                    throw new CanBusException($"write() to CAN socket failed with errno {Marshal.GetLastPInvokeError()}.");
                }
            }
        }

        private unsafe void ReadLoop()
        {
            var buffer = new byte[SocketCanFrameCodec.CanFdMtu];
            var pollFd = new SocketCanNative.PollFd { Fd = _fd, Events = SocketCanNative.PollIn };
            while (!_stopping.IsCancellationRequested)
            {
                // poll() with a timeout lets the thread observe shutdown without relying on close() semantics.
                var ready = SocketCanNative.poll(&pollFd, 1, 200);
                if (ready <= 0 || (pollFd.ReturnedEvents & SocketCanNative.PollIn) == 0)
                {
                    continue;
                }

                nint count;
                fixed (byte* pBuffer = buffer)
                {
                    count = SocketCanNative.read(_fd, pBuffer, buffer.Length);
                }

                if (count > 0 && SocketCanFrameCodec.TryDecode(buffer.AsSpan(0, (int)count), _timeProvider.GetUtcNow(), out var frame))
                {
                    if (_received.Writer.TryWrite(frame!))
                    {
                        Statistics.OnReceived();
                    }
                    else
                    {
                        Statistics.OnDropped();
                    }
                }
            }
        }
    }
}
