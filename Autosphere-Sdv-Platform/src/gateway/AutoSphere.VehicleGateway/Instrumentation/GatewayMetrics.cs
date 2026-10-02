using System.Diagnostics.Metrics;

namespace AutoSphere.VehicleGateway.Instrumentation;

/// <summary>
/// Gateway performance instrumentation (meter <c>AutoSphere.Gateway</c>), observable with
/// <c>dotnet-counters monitor --counters AutoSphere.Gateway -n AutoSphere.VehicleGateway</c>.
/// The same numbers are also attached to every telemetry message for end-to-end analysis.
/// </summary>
public sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "AutoSphere.Gateway";

    private readonly Meter _meter = new(MeterName, "1.0");
    private readonly Counter<long> _framesReceived;
    private readonly Counter<long> _framesRejected;
    private readonly Counter<long> _mqttPublished;
    private readonly Histogram<double> _decodeMicroseconds;
    private readonly Histogram<double> _canToMqttMilliseconds;
    private readonly Histogram<double> _diagnosticMilliseconds;
    private readonly object _windowGate = new();

    private long _windowReceived;
    private long _windowDecoded;
    private long _windowRejected;
    private double _windowDecodeSum;
    private double _windowDecodeMax;
    private long _windowStartTicks = Environment.TickCount64;

    public GatewayMetrics()
    {
        _framesReceived = _meter.CreateCounter<long>("autosphere.gateway.can.frames_received", "{frame}");
        _framesRejected = _meter.CreateCounter<long>("autosphere.gateway.can.frames_rejected", "{frame}", "Frames failing E2E checks or unknown ids");
        _mqttPublished = _meter.CreateCounter<long>("autosphere.gateway.mqtt.messages_published", "{message}");
        _decodeMicroseconds = _meter.CreateHistogram<double>("autosphere.gateway.can.decode_duration", "us", "CAN frame decode + normalization time");
        _canToMqttMilliseconds = _meter.CreateHistogram<double>("autosphere.gateway.can_to_mqtt_latency", "ms", "Age of the newest signal when its telemetry message is published");
        _diagnosticMilliseconds = _meter.CreateHistogram<double>("autosphere.gateway.diagnostic_duration", "ms", "Duration of diagnostic operations");
    }

    public void FrameReceived(bool decoded, double decodeMicroseconds)
    {
        _framesReceived.Add(1);
        if (!decoded)
        {
            _framesRejected.Add(1);
        }
        else
        {
            _decodeMicroseconds.Record(decodeMicroseconds);
        }

        lock (_windowGate)
        {
            _windowReceived++;
            if (decoded)
            {
                _windowDecoded++;
                _windowDecodeSum += decodeMicroseconds;
                _windowDecodeMax = Math.Max(_windowDecodeMax, decodeMicroseconds);
            }
            else
            {
                _windowRejected++;
            }
        }
    }

    public void MessagePublished() => _mqttPublished.Add(1);

    public void CanToMqttLatency(double milliseconds) => _canToMqttMilliseconds.Record(milliseconds);

    public void DiagnosticCompleted(double milliseconds) => _diagnosticMilliseconds.Record(milliseconds);

    /// <summary>Returns statistics since the previous call and starts a new window.</summary>
    public (long Received, long Decoded, long Rejected, double FramesPerSecond, double AverageDecodeUs, double MaxDecodeUs) TakeWindow()
    {
        lock (_windowGate)
        {
            var now = Environment.TickCount64;
            var seconds = Math.Max((now - _windowStartTicks) / 1000.0, 0.001);
            var result = (_windowReceived, _windowDecoded, _windowRejected, _windowReceived / seconds,
                _windowDecoded == 0 ? 0 : _windowDecodeSum / _windowDecoded, _windowDecodeMax);
            _windowReceived = _windowDecoded = _windowRejected = 0;
            _windowDecodeSum = _windowDecodeMax = 0;
            _windowStartTicks = now;
            return result;
        }
    }

    public void Dispose() => _meter.Dispose();
}
