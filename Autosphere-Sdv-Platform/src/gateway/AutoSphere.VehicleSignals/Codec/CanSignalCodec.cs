using AutoSphere.CanBus;
using AutoSphere.VehicleSignals.Database;

namespace AutoSphere.VehicleSignals.Codec;

public enum E2EStatus
{
    NotProtected = 0,
    Ok = 1,
    CrcError = 2,
}

/// <summary>A signal value decoded from a frame.</summary>
public readonly record struct DecodedSignal(CanSignalDefinition Definition, long RawValue, double PhysicalValue, bool IsInRange)
{
    public string? Label => Definition.LabelFor(RawValue);
}

/// <summary>All signals of one received frame.</summary>
public sealed record DecodedMessage(CanMessageDefinition Definition, CanFrame Frame, E2EStatus E2EStatus, int? AliveCounter, IReadOnlyList<DecodedSignal> Signals);

/// <summary>Decodes raw CAN frames into physical signal values.</summary>
public interface ICanSignalDecoder
{
    /// <summary>Returns <c>false</c> for frames whose id is not in the CAN database or whose length is wrong.</summary>
    bool TryDecode(CanFrame frame, out DecodedMessage? message);
}

/// <summary>Encodes physical signal values into CAN frames (used by the ECU simulators).</summary>
public interface ICanSignalEncoder
{
    byte[] Encode(CanMessageDefinition message, IReadOnlyDictionary<string, double> physicalValues, int aliveCounter);
}

/// <summary>
/// Database-driven codec: all decoding knowledge comes from <see cref="CanDatabase"/>, there is no
/// per-signal code anywhere in the system.
/// </summary>
public sealed class CanSignalCodec : ICanSignalDecoder, ICanSignalEncoder
{
    private readonly CanDatabase _database;

    public CanSignalCodec(CanDatabase database) => _database = database;

    public CanDatabase Database => _database;

    public bool TryDecode(CanFrame frame, out DecodedMessage? message)
    {
        ArgumentNullException.ThrowIfNull(frame);
        message = null;
        if (frame.IsExtended || !_database.TryGetMessage(frame.Id, out var definition) || frame.Length != definition.Length)
        {
            return false;
        }

        var data = frame.Data.Span;
        var e2e = E2EStatus.NotProtected;
        int? counter = null;
        if (definition.E2EDataId is { } dataId)
        {
            e2e = E2EProtection.CrcIsValid(dataId, data) ? E2EStatus.Ok : E2EStatus.CrcError;
            counter = E2EProtection.ReadCounter(data);
        }

        var signals = new DecodedSignal[definition.Signals.Count];
        for (var i = 0; i < signals.Length; i++)
        {
            var signal = definition.Signals[i];
            var raw = BitCodec.Extract(data, signal.StartBit, signal.Length, signal.ByteOrder);
            var value = signal.IsSigned ? BitCodec.ToSigned(raw, signal.Length) : (long)raw;
            var physical = signal.ToPhysical(value);
            signals[i] = new DecodedSignal(signal, value, physical, signal.IsInRange(physical));
        }

        message = new DecodedMessage(definition, frame, e2e, counter, signals);
        return true;
    }

    public byte[] Encode(CanMessageDefinition message, IReadOnlyDictionary<string, double> physicalValues, int aliveCounter)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(physicalValues);

        var payload = new byte[message.Length];
        foreach (var signal in message.Signals)
        {
            if (!physicalValues.TryGetValue(signal.Name, out var physical))
            {
                continue;
            }

            // Saturate to the representable raw range, as an ECU would clamp before transmission.
            var raw = Math.Clamp(signal.ToRaw(physical), BitCodec.MinRaw(signal.Length, signal.IsSigned), BitCodec.MaxRaw(signal.Length, signal.IsSigned));
            var bits = signal.IsSigned ? BitCodec.FromSigned(raw, signal.Length) : (ulong)raw;
            BitCodec.Insert(payload, signal.StartBit, signal.Length, signal.ByteOrder, bits);
        }

        if (message.E2EDataId is { } dataId)
        {
            E2EProtection.Protect(dataId, payload, aliveCounter);
        }

        return payload;
    }
}
