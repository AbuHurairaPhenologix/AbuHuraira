using AutoSphere.CanBus;
using AutoSphere.CanBus.InMemory;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.CanBus.SocketCan;
using AutoSphere.CanBus.Udp;

namespace AutoSphere.UnitTests.CanBus;

public sealed class CanFrameTests
{
    [Fact]
    public void Standard_ids_are_limited_to_11_bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x800, new byte[1], DateTimeOffset.UtcNow));
        Assert.Equal(0x800u, new CanFrame(0x800, new byte[1], DateTimeOffset.UtcNow, isExtended: true).Id);
    }

    [Fact]
    public void Classic_frames_carry_at_most_eight_bytes_and_fd_frames_use_valid_lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x1, new byte[9], DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x1, new byte[9], DateTimeOffset.UtcNow, isFd: true));
        Assert.Equal(13, new CanFrame(0x1, new byte[32], DateTimeOffset.UtcNow, isFd: true).Dlc);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(33, 48)]
    [InlineData(64, 64)]
    public void Fd_lengths_round_up(int length, int expected) => Assert.Equal(expected, CanDlc.RoundUpToFdLength(length));
}

public sealed class InMemoryCanBusTests
{
    [Fact]
    public async Task Frames_are_delivered_to_other_channels_but_not_echoed_to_sender()
    {
        await using var bus = new InMemoryCanBus();
        await using var sender = await bus.OpenChannelAsync("sender", cancellationToken: TestContext.Current.CancellationToken);
        await using var receiver = await bus.OpenChannelAsync("receiver", cancellationToken: TestContext.Current.CancellationToken);

        await sender.WriteAsync(new CanFrame(0x123, new byte[] { 1, 2, 3 }, default), TestContext.Current.CancellationToken);

        var received = await receiver.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0x123u, received.Id);
        Assert.Equal("sender", received.Source);
        Assert.NotEqual(default, received.Timestamp);
        Assert.Equal(0, sender.Statistics.FramesReceived);
    }

    [Fact]
    public async Task Acceptance_filters_are_applied()
    {
        await using var bus = new InMemoryCanBus();
        await using var sender = await bus.OpenChannelAsync("sender", cancellationToken: TestContext.Current.CancellationToken);
        await using var filtered = await bus.OpenChannelAsync("filtered", [CanFilter.Exact(0x300)], TestContext.Current.CancellationToken);

        await sender.WriteAsync(new CanFrame(0x100, new byte[8], default), TestContext.Current.CancellationToken);
        await sender.WriteAsync(new CanFrame(0x300, new byte[8], default), TestContext.Current.CancellationToken);

        var received = await filtered.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0x300u, received.Id);
        Assert.Equal(1, filtered.Statistics.FramesReceived);
    }

    [Fact]
    public async Task Full_receive_queue_drops_oldest_frames()
    {
        await using var bus = new InMemoryCanBus(receiveQueueCapacity: 2);
        await using var sender = await bus.OpenChannelAsync("sender", cancellationToken: TestContext.Current.CancellationToken);
        await using var receiver = await bus.OpenChannelAsync("receiver", cancellationToken: TestContext.Current.CancellationToken);

        for (byte i = 0; i < 5; i++)
        {
            await sender.WriteAsync(new CanFrame(0x10, new[] { i }, default), TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, receiver.Statistics.FramesDropped);
        Assert.Equal(3, (await receiver.ReadAsync(TestContext.Current.CancellationToken)).Data.Span[0]);
    }
}

public sealed class WireFormatTests
{
    [Fact]
    public void Socketcan_classic_frame_layout_matches_kernel_struct()
    {
        var frame = new CanFrame(0x301, new byte[] { 0xAA, 0xBB }, default);
        var buffer = new byte[SocketCanFrameCodec.CanFdMtu];

        var length = SocketCanFrameCodec.Encode(frame, buffer);

        Assert.Equal(16, length);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x02, 0, 0, 0, 0xAA, 0xBB, 0, 0, 0, 0, 0, 0 }, buffer[..16]);
        Assert.True(SocketCanFrameCodec.TryDecode(buffer.AsSpan(0, 16), DateTimeOffset.UnixEpoch, out var decoded));
        Assert.Equal(frame.Id, decoded!.Id);
        Assert.Equal(frame.Data.ToArray(), decoded.Data.ToArray());
    }

    [Fact]
    public void Socketcan_extended_fd_frame_sets_flags()
    {
        var frame = new CanFrame(0x18DAF110, new byte[12], default, isExtended: true, isFd: true);
        var buffer = new byte[SocketCanFrameCodec.CanFdMtu];

        Assert.Equal(72, SocketCanFrameCodec.Encode(frame, buffer));
        Assert.Equal(0x80, buffer[3] & 0x80); // CAN_EFF_FLAG
        Assert.True(SocketCanFrameCodec.TryDecode(buffer, default, out var decoded));
        Assert.True(decoded!.IsExtended && decoded.IsFd);
        Assert.Equal(0x18DAF110u, decoded.Id);
    }

    [Fact]
    public void Socketcan_error_frames_are_ignored()
    {
        var buffer = new byte[16];
        BitConverter.GetBytes(SocketCanFrameCodec.ErrFlag | 0x4).CopyTo(buffer, 0);

        Assert.False(SocketCanFrameCodec.TryDecode(buffer, default, out _));
    }

    [Fact]
    public void Udp_wire_format_round_trips()
    {
        var frame = new CanFrame(0x7E8, new byte[] { 0x03, 0x7F, 0x22, 0x31 }, default);
        var buffer = new byte[CanFrameWireFormat.GetEncodedLength(frame)];

        CanFrameWireFormat.Encode(frame, buffer);

        Assert.True(CanFrameWireFormat.TryDecode(buffer, DateTimeOffset.UnixEpoch, out var decoded));
        Assert.Equal(frame.Id, decoded!.Id);
        Assert.Equal(frame.Data.ToArray(), decoded.Data.ToArray());
        Assert.False(CanFrameWireFormat.TryDecode(buffer.AsSpan(0, 5), default, out _));
    }
}

public sealed class IsoTpTests
{
    [Fact]
    public void Pci_parsing_recognises_all_frame_types()
    {
        Assert.True(IsoTpPci.TryParse(IsoTpPci.SingleFrame(new byte[] { 0x22, 0xF1, 0x89 }, 0xCC), out var sf));
        Assert.Equal((IsoTpFrameType.SingleFrame, 3), (sf.Type, sf.MessageLength));

        Assert.True(IsoTpPci.TryParse(IsoTpPci.FirstFrame(300, new byte[6], 0xCC), out var ff));
        Assert.Equal((IsoTpFrameType.FirstFrame, 300), (ff.Type, ff.MessageLength));

        Assert.True(IsoTpPci.TryParse(IsoTpPci.ConsecutiveFrame(17, new byte[7], 0xCC), out var cf));
        Assert.Equal((IsoTpFrameType.ConsecutiveFrame, 1), (cf.Type, cf.SequenceNumber));

        Assert.True(IsoTpPci.TryParse(IsoTpPci.FlowControl(IsoTpFlowStatus.ContinueToSend, 8, 0x05, 0xCC), out var fc));
        Assert.Equal((IsoTpFrameType.FlowControl, (byte)8, (byte)5), (fc.Type, fc.BlockSize, fc.SeparationTimeMin));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x0A, 10_000)]
    [InlineData(0xF1, 100)]
    [InlineData(0xF9, 900)]
    [InlineData(0x80, 127_000)]
    public void Separation_time_encoding(byte stMin, int expectedMicroseconds) =>
        Assert.Equal(TimeSpan.FromMicroseconds(expectedMicroseconds), IsoTpPci.SeparationTime(stMin));

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(62)]
    [InlineData(1500)]
    [InlineData(4095)]
    public async Task Messages_are_segmented_and_reassembled(int length)
    {
        await using var bus = new InMemoryCanBus();
        await using var tester = await IsoTpChannel.OpenAsync(bus, "tester", 0x7E0, 0x7E8, cancellationToken: TestContext.Current.CancellationToken);
        await using var ecu = await IsoTpChannel.OpenAsync(bus, "ecu", 0x7E8, 0x7E0,
            new IsoTpOptions { BlockSize = 4 }, cancellationToken: TestContext.Current.CancellationToken);
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

        await tester.SendAsync(payload, TestContext.Current.CancellationToken);
        var received = await ecu.ReceiveAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task Missing_flow_control_times_out()
    {
        await using var bus = new InMemoryCanBus();
        await using var tester = await IsoTpChannel.OpenAsync(bus, "tester", 0x7E0, 0x7E8,
            new IsoTpOptions { FlowControlTimeout = TimeSpan.FromMilliseconds(100) }, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IsoTpException>(() => tester.SendAsync(new byte[20], TestContext.Current.CancellationToken));
    }
}
