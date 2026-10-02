using System.Globalization;
using System.Net;
using AutoSphere.CanBus.InMemory;
using AutoSphere.CanBus.SocketCan;
using AutoSphere.CanBus.Udp;
using Microsoft.Extensions.Logging;

namespace AutoSphere.CanBus;

public enum CanTransport
{
    /// <summary>Simulated bus inside the current process (default for development and tests).</summary>
    InMemory = 0,

    /// <summary>Local segment bridged to other processes over UDP (cross-platform multi-process setup).</summary>
    Udp = 1,

    /// <summary>Linux SocketCAN interface such as <c>vcan0</c> or a physical <c>can0</c>.</summary>
    SocketCan = 2,
}

/// <summary>Configuration section <c>CanBus</c>.</summary>
public sealed class CanBusOptions
{
    public const string SectionName = "CanBus";

    public CanTransport Transport { get; set; } = CanTransport.InMemory;

    /// <summary>SocketCAN interface name.</summary>
    public string Interface { get; set; } = "vcan0";

    /// <summary>Enable CAN-FD frames on SocketCAN.</summary>
    public bool EnableFd { get; set; }

    /// <summary>Local UDP port for the <see cref="CanTransport.Udp"/> transport.</summary>
    public int UdpLocalPort { get; set; } = 20000;

    /// <summary>UDP peers as <c>host:port</c>.</summary>
    public List<string> UdpPeers { get; set; } = [];
}

public static class CanBusFactory
{
    public static async Task<ICanBus> CreateAsync(CanBusOptions options, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        switch (options.Transport)
        {
            case CanTransport.InMemory:
                return new InMemoryCanBus(timeProvider);
            case CanTransport.Udp:
                var peers = new List<IPEndPoint>();
                foreach (var peer in options.UdpPeers)
                {
                    peers.Add(await ResolveAsync(peer));
                }

                return await UdpCanBus.CreateAsync(options.UdpLocalPort, peers, loggerFactory.CreateLogger<UdpCanBus>(), timeProvider);
            case CanTransport.SocketCan:
                if (!OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException("The SocketCan transport requires Linux. Use InMemory or Udp on this platform.");
                }

                return new SocketCanBus(options.Interface, options.EnableFd, timeProvider);
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Transport, "Unknown CAN transport.");
        }
    }

    private static async Task<IPEndPoint> ResolveAsync(string hostAndPort)
    {
        var separator = hostAndPort.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(hostAndPort[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new FormatException($"UDP peer '{hostAndPort}' must have the form host:port.");
        }

        var host = hostAndPort[..separator];
        if (IPAddress.TryParse(host, out var address))
        {
            return new IPEndPoint(address, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host);
        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
        return new IPEndPoint(ipv4, port);
    }
}
