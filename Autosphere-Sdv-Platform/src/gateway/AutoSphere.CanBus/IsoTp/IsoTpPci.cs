namespace AutoSphere.CanBus.IsoTp;

public enum IsoTpFrameType : byte
{
    SingleFrame = 0x0,
    FirstFrame = 0x1,
    ConsecutiveFrame = 0x2,
    FlowControl = 0x3,
}

public enum IsoTpFlowStatus : byte
{
    ContinueToSend = 0x0,
    Wait = 0x1,
    Overflow = 0x2,
}

/// <summary>Parsed protocol control information (PCI) of one ISO-TP frame.</summary>
public readonly record struct IsoTpFrameInfo(
    IsoTpFrameType Type,
    int MessageLength,
    int SequenceNumber,
    IsoTpFlowStatus FlowStatus,
    byte BlockSize,
    byte SeparationTimeMin,
    int DataOffset,
    int DataLength);

/// <summary>
/// Encoding/decoding of ISO-TP (ISO 15765-2 inspired) protocol control information for classic CAN
/// with normal addressing. Escape sequences for messages longer than 4095 bytes and CAN-FD single frames
/// longer than 7 bytes are intentionally not supported.
/// </summary>
public static class IsoTpPci
{
    public const int FrameLength = 8;
    public const int MaxSingleFramePayload = 7;
    public const int FirstFramePayload = 6;
    public const int ConsecutiveFramePayload = 7;
    public const int MaxMessageLength = 4095;

    public static bool TryParse(ReadOnlySpan<byte> frame, out IsoTpFrameInfo info)
    {
        info = default;
        if (frame.Length == 0)
        {
            return false;
        }

        var type = (IsoTpFrameType)(frame[0] >> 4);
        switch (type)
        {
            case IsoTpFrameType.SingleFrame:
            {
                var length = frame[0] & 0x0F;
                if (length is 0 or > MaxSingleFramePayload || frame.Length < 1 + length)
                {
                    return false;
                }

                info = new IsoTpFrameInfo(type, length, 0, default, 0, 0, 1, length);
                return true;
            }

            case IsoTpFrameType.FirstFrame:
            {
                if (frame.Length < FrameLength)
                {
                    return false;
                }

                var length = ((frame[0] & 0x0F) << 8) | frame[1];
                if (length <= MaxSingleFramePayload)
                {
                    return false;
                }

                info = new IsoTpFrameInfo(type, length, 0, default, 0, 0, 2, FirstFramePayload);
                return true;
            }

            case IsoTpFrameType.ConsecutiveFrame:
                info = new IsoTpFrameInfo(type, 0, frame[0] & 0x0F, default, 0, 0, 1, frame.Length - 1);
                return true;

            case IsoTpFrameType.FlowControl:
            {
                if (frame.Length < 3 || (frame[0] & 0x0F) > (byte)IsoTpFlowStatus.Overflow)
                {
                    return false;
                }

                info = new IsoTpFrameInfo(type, 0, 0, (IsoTpFlowStatus)(frame[0] & 0x0F), frame[1], frame[2], 3, 0);
                return true;
            }

            default:
                return false;
        }
    }

    public static byte[] SingleFrame(ReadOnlySpan<byte> payload, byte padding)
    {
        if (payload.Length is 0 or > MaxSingleFramePayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var frame = Padded(padding);
        frame[0] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] FirstFrame(int totalLength, ReadOnlySpan<byte> firstChunk, byte padding)
    {
        if (totalLength is <= MaxSingleFramePayload or > MaxMessageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(totalLength));
        }

        var frame = Padded(padding);
        frame[0] = (byte)(0x10 | (totalLength >> 8));
        frame[1] = (byte)(totalLength & 0xFF);
        firstChunk[..FirstFramePayload].CopyTo(frame.AsSpan(2));
        return frame;
    }

    public static byte[] ConsecutiveFrame(int sequenceNumber, ReadOnlySpan<byte> chunk, byte padding)
    {
        if (chunk.Length is 0 or > ConsecutiveFramePayload)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk));
        }

        var frame = Padded(padding);
        frame[0] = (byte)(0x20 | (sequenceNumber & 0x0F));
        chunk.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] FlowControl(IsoTpFlowStatus status, byte blockSize, byte separationTimeMin, byte padding)
    {
        var frame = Padded(padding);
        frame[0] = (byte)(0x30 | (byte)status);
        frame[1] = blockSize;
        frame[2] = separationTimeMin;
        return frame;
    }

    /// <summary>Converts the STmin byte to a delay. 0x00–0x7F are milliseconds, 0xF1–0xF9 are 100–900 µs.</summary>
    public static TimeSpan SeparationTime(byte stMin) => stMin switch
    {
        <= 0x7F => TimeSpan.FromMilliseconds(stMin),
        >= 0xF1 and <= 0xF9 => TimeSpan.FromMicroseconds((stMin - 0xF0) * 100),
        _ => TimeSpan.FromMilliseconds(0x7F), // reserved values: use the maximum, as required by the standard
    };

    private static byte[] Padded(byte padding)
    {
        var frame = new byte[FrameLength];
        frame.AsSpan().Fill(padding);
        return frame;
    }
}
