using System.Buffers.Binary;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Diagnostics.Uds;

/// <summary>A parsed UDS response.</summary>
public sealed class UdsResponse
{
    public UdsResponse(byte[] raw)
    {
        if (raw.Length == 0)
        {
            throw new ArgumentException("Empty UDS response.", nameof(raw));
        }

        Raw = raw;
    }

    public byte[] Raw { get; }

    public bool IsNegative => Raw[0] == UdsProtocol.NegativeResponseSid;

    public bool IsPositive => !IsNegative;

    /// <summary>The service the response belongs to.</summary>
    public byte ServiceId => IsNegative ? (Raw.Length > 1 ? Raw[1] : (byte)0) : (byte)(Raw[0] - UdsProtocol.PositiveResponseOffset);

    public NegativeResponseCode? NegativeResponseCode => IsNegative && Raw.Length > 2 ? (NegativeResponseCode)Raw[2] : null;

    /// <summary>Response parameters after the response SID.</summary>
    public ReadOnlySpan<byte> Data => Raw.AsSpan(1);
}

/// <summary>One request/response pair, recorded for traceability (and for teaching: the raw bytes are shown in the UI).</summary>
public sealed record UdsExchange(UdsService Service, byte[] Request, byte[]? Response, TimeSpan Duration, NegativeResponseCode? NegativeResponse);

/// <summary>Collects the exchanges of one diagnostic operation.</summary>
public sealed class UdsTrace
{
    private readonly List<UdsExchange> _exchanges = [];

    public IReadOnlyList<UdsExchange> Exchanges => _exchanges;

    public void Add(UdsExchange exchange)
    {
        lock (_exchanges)
        {
            _exchanges.Add(exchange);
        }
    }
}

public class UdsException : Exception
{
    public UdsException()
    {
    }

    public UdsException(string message)
        : base(message)
    {
    }

    public UdsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class UdsNegativeResponseException : UdsException
{
    public UdsNegativeResponseException(UdsService service, NegativeResponseCode code)
        : base($"{service} rejected with NRC 0x{(byte)code:X2} ({UdsProtocol.Describe(code)}).")
    {
        Service = service;
        Code = code;
    }

    public UdsService Service { get; }

    public NegativeResponseCode Code { get; }
}

public sealed class UdsTimeoutException : UdsException
{
    public UdsTimeoutException(UdsService service, TimeSpan timeout)
        : base($"No response to {service} within {timeout.TotalMilliseconds} ms (P2 timeout).")
    {
        Service = service;
    }

    public UdsService Service { get; }
}

/// <summary>A DTC entry as returned by ReadDTCInformation.</summary>
public sealed record UdsDtcRecord(DtcCode Code, DtcStatusBits Status)
{
    public bool TestFailed => Status.HasFlag(DtcStatusBits.TestFailed);

    public bool Confirmed => Status.HasFlag(DtcStatusBits.ConfirmedDtc);
}

/// <summary>A decoded value from a DTC snapshot record.</summary>
public sealed record UdsSnapshotValue(DataIdentifierDefinition Definition, string Text, double? Numeric);

/// <summary>Encoding and parsing helpers for the supported services.</summary>
public static class UdsCodec
{
    public static byte[] EncodeDtc(DtcCode code)
    {
        var raw = code.ToUdsDtc();
        return [(byte)(raw >> 16), (byte)(raw >> 8), (byte)raw];
    }

    public static uint ReadDtc(ReadOnlySpan<byte> data) => ((uint)data[0] << 16) | ((uint)data[1] << 8) | data[2];

    /// <summary>Parses the parameters of a positive <c>0x59 0x02</c> response.</summary>
    public static IReadOnlyList<UdsDtcRecord> ParseDtcsByStatusMask(ReadOnlySpan<byte> data)
    {
        // data: [subFunction, statusAvailabilityMask, (DTC high, DTC mid, DTC low, status)*]
        if (data.Length < 2 || (data.Length - 2) % 4 != 0 || data[0] != (byte)ReadDtcSubFunction.ReportDtcByStatusMask)
        {
            throw new UdsException("Malformed ReadDTCInformation(reportDTCByStatusMask) response.");
        }

        var records = new List<UdsDtcRecord>();
        for (var offset = 2; offset < data.Length; offset += 4)
        {
            records.Add(new UdsDtcRecord(DtcCode.FromUdsDtc(ReadDtc(data[offset..])), (DtcStatusBits)data[offset + 3]));
        }

        return records;
    }

    /// <summary>Encodes the parameters of a positive <c>0x59 0x04</c> response (server side).</summary>
    public static byte[] EncodeSnapshot(DtcCode code, DtcStatusBits status, byte recordNumber, IReadOnlyList<(DataIdentifierDefinition Definition, byte[] Data)> values)
    {
        var buffer = new List<byte> { (byte)ReadDtcSubFunction.ReportDtcSnapshotRecordByDtcNumber };
        buffer.AddRange(EncodeDtc(code));
        buffer.Add((byte)status);
        if (values.Count > 0)
        {
            buffer.Add(recordNumber);
            buffer.Add((byte)values.Count);
            foreach (var (definition, data) in values)
            {
                buffer.Add((byte)(definition.Identifier >> 8));
                buffer.Add((byte)definition.Identifier);
                buffer.AddRange(data);
            }
        }

        return [.. buffer];
    }

    /// <summary>Parses the parameters of a positive <c>0x59 0x04</c> response.</summary>
    public static IReadOnlyList<UdsSnapshotValue> ParseSnapshot(ReadOnlySpan<byte> data)
    {
        // data: [subFunction, DTC(3), status, (recordNumber, numberOfIdentifiers, (DID(2), value)*)?]
        if (data.Length < 5 || data[0] != (byte)ReadDtcSubFunction.ReportDtcSnapshotRecordByDtcNumber)
        {
            throw new UdsException("Malformed ReadDTCInformation(reportDTCSnapshotRecordByDTCNumber) response.");
        }

        var values = new List<UdsSnapshotValue>();
        if (data.Length == 5)
        {
            return values; // no snapshot stored
        }

        int count = data[6];
        var offset = 7;
        for (var i = 0; i < count; i++)
        {
            var identifier = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (!DataIdentifierCatalog.TryGet(identifier, out var definition))
            {
                throw new UdsException($"Snapshot contains unknown DID 0x{identifier:X4}; its length cannot be determined.");
            }

            var (text, numeric) = DataIdentifierCatalog.Decode(definition, data[offset..]);
            values.Add(new UdsSnapshotValue(definition, text, numeric));
            offset += definition.Length;
        }

        return values;
    }
}
