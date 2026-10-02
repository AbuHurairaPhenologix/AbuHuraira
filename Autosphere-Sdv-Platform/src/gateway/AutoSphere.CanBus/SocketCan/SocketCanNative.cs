using System.Runtime.InteropServices;

namespace AutoSphere.CanBus.SocketCan;

/// <summary>Minimal libc bindings for Linux SocketCAN raw sockets.</summary>
internal static unsafe partial class SocketCanNative
{
    public const int PfCan = 29;
    public const int SockRaw = 3;
    public const int CanRaw = 1;
    public const int SolCanRaw = 101; // SOL_CAN_BASE (100) + CAN_RAW (1)
    public const int CanRawFilter = 1;
    public const int CanRawFdFrames = 5;
    public const short PollIn = 0x0001;
    public const int SockAddrCanSize = 24; // sizeof(struct sockaddr_can) on 64-bit Linux

    private const string LibC = "libc";

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int socket(int domain, int type, int protocol);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int bind(int socket, byte* address, int addressLength);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int setsockopt(int socket, int level, int optionName, void* optionValue, uint optionLength);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nint count);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nint count);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint count, int timeoutMilliseconds);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(LibC, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint if_nametoindex(string interfaceName);
}
