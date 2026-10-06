using System.Threading.Channels;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Ingestion;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Infrastructure.Queue;

/// <summary>
/// Bounded in-process channel between request middleware and the ingestion worker. Writes never block a request:
/// when full, the event is counted as dropped (exposed in system status) instead of slowing the user down.
/// </summary>
public sealed class ChannelEventQueue : IEventQueue
{
    private readonly Channel<RawEventInput> _channel;
    private long _dropped;

    public ChannelEventQueue(IOptions<PipelineOptions> options)
    {
        _channel = Channel.CreateBounded<RawEventInput>(new BoundedChannelOptions(options.Value.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<RawEventInput> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public int ApproximateCount => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    public bool TryEnqueue(RawEventInput rawEvent)
    {
        if (_channel.Writer.TryWrite(rawEvent))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }
}
