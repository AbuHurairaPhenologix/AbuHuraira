using System.Globalization;

namespace AutoSphere.CanBus;

/// <summary>
/// An immutable CAN or CAN-FD data frame.
/// </summary>
/// <remarks>
/// Remote (RTR) and error frames are not modelled: they are not used by any AutoSphere ECU.
/// </remarks>
public sealed record CanFrame
{
    public const uint MaxStandardId = 0x7FF;
    public const uint MaxExtendedId = 0x1FFF_FFFF;
    public const int MaxClassicLength = 8;
    public const int MaxFdLength = 64;

    public CanFrame(uint id, ReadOnlyMemory<byte> data, DateTimeOffset timestamp, bool isExtended = false, bool isFd = false, string? source = null)
    {
        if (id > (isExtended ? MaxExtendedId : MaxStandardId))
        {
            throw new ArgumentOutOfRangeException(nameof(id), $"CAN id 0x{id:X} exceeds the {(isExtended ? "29" : "11")}-bit range.");
        }

        if (!isFd && data.Length > MaxClassicLength)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"Classic CAN frames carry at most {MaxClassicLength} bytes.");
        }

        if (isFd && !CanDlc.IsValidFdLength(data.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"{data.Length} bytes is not a valid CAN-FD payload length.");
        }

        Id = id;
        Data = data;
        Timestamp = timestamp;
        IsExtended = isExtended;
        IsFd = isFd;
        Source = source;
    }

    /// <summary>11-bit (standard) or 29-bit (extended) arbitration id.</summary>
    public uint Id { get; }

    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Receive timestamp (or transmit timestamp before the frame is written).</summary>
    public DateTimeOffset Timestamp { get; init; }

    public bool IsExtended { get; }

    public bool IsFd { get; }

    /// <summary>Logical sender, e.g. <c>BMS-001</c>. Not transmitted on a real bus; diagnostic aid only.</summary>
    public string? Source { get; init; }

    /// <summary>Data length code as it would appear on the wire.</summary>
    public byte Dlc => CanDlc.FromLength(Data.Length);

    public int Length => Data.Length;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(IsExtended ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture))} [{Length}] {Convert.ToHexString(Data.Span)}{(Source is null ? string.Empty : $" ({Source})")}");
}

/// <summary>DLC ⇄ payload length mapping for classic CAN and CAN-FD (ISO 11898-1:2015).</summary>
public static class CanDlc
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public static bool IsValidFdLength(int length) => Array.IndexOf(FdLengths, length) >= 0;

    public static byte FromLength(int length)
    {
        var index = Array.IndexOf(FdLengths, length);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"{length} is not a valid CAN/CAN-FD length.");
        }

        return (byte)index;
    }

    public static int ToLength(byte dlc) => dlc < FdLengths.Length
        ? FdLengths[dlc]
        : throw new ArgumentOutOfRangeException(nameof(dlc));

    /// <summary>Smallest valid CAN-FD length that can hold <paramref name="length"/> bytes.</summary>
    public static int RoundUpToFdLength(int length) =>
        FdLengths.FirstOrDefault(l => l >= length, -1) is var rounded and >= 0
            ? rounded
            : throw new ArgumentOutOfRangeException(nameof(length));
}

/// <summary>An acceptance filter with SocketCAN semantics: a frame matches when <c>(frame.Id &amp; Mask) == (Id &amp; Mask)</c>.</summary>
public readonly record struct CanFilter(uint Id, uint Mask)
{
    /// <summary>Filter set for transmit-only channels: matches no valid frame.</summary>
    public static IReadOnlyList<CanFilter> ReceiveNothing { get; } = [new(uint.MaxValue, uint.MaxValue)];

    public static CanFilter Exact(uint id) => new(id, CanFrame.MaxExtendedId);

    public static CanFilter Range(uint baseId, uint mask) => new(baseId, mask);

    public bool Matches(CanFrame frame) => (frame.Id & Mask) == (Id & Mask);

    public static bool MatchesAny(IReadOnlyList<CanFilter>? filters, CanFrame frame)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Matches(frame))
            {
                return true;
            }
        }

        return false;
    }
}
