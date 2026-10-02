using AutoSphere.CanBus;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.CanBus.SocketCan;

namespace AutoSphere.IntegrationTests;

/// <summary>
/// Exercises the Linux SocketCAN transport against a real (virtual) CAN interface. Runs only when
/// <c>AUTOSPHERE_SOCKETCAN_INTERFACE</c> names an existing interface (e.g. <c>vcan0</c>, created by
/// <c>scripts/setup-vcan.sh</c>); otherwise the tests are reported as skipped.
/// </summary>
public sealed class SocketCanTests
{
    private static readonly string? Interface = Environment.GetEnvironmentVariable("AUTOSPHERE_SOCKETCAN_INTERFACE");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_are_exchanged_between_raw_sockets()
    {
        var bus = CreateBusOrSkip();
        await using var sender = await bus.OpenChannelAsync("sender", cancellationToken: Ct);
        await using var receiver = await bus.OpenChannelAsync("receiver", [CanFilter.Exact(0x301)], Ct);

        await sender.WriteAsync(new CanFrame(0x100, new byte[] { 1 }, DateTimeOffset.UtcNow), Ct); // filtered out
        await sender.WriteAsync(new CanFrame(0x301, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, DateTimeOffset.UtcNow), Ct);

        var frame = await receiver.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0x301u, frame.Id);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, frame.Data.ToArray());
    }

    [Fact]
    public async Task Iso_tp_segmentation_works_over_socketcan()
    {
        var bus = CreateBusOrSkip();
        await using var tester = await IsoTpChannel.OpenAsync(bus, "tester", 0x7E2, 0x7EA, cancellationToken: Ct);
        await using var ecu = await IsoTpChannel.OpenAsync(bus, "ecu", 0x7EA, 0x7E2, cancellationToken: Ct);
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        await tester.SendAsync(payload, Ct);

        Assert.Equal(payload, await ecu.ReceiveAsync(TimeSpan.FromSeconds(5), Ct));
    }

    private static ICanBus CreateBusOrSkip()
    {
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Interface))
        {
            return new SocketCanBus(Interface);
        }

        Assert.Skip("Set AUTOSPHERE_SOCKETCAN_INTERFACE (Linux with vcan) to run SocketCAN tests.");
        return null!;
    }
}
