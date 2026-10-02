using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.SharedKernel.Ota;

/// <summary>Header of a simulated firmware image.</summary>
public sealed record FirmwareImageHeader(EcuType EcuType, SoftwareVersion Version, FirmwareBootBehavior BootBehavior, string BuildId);

/// <summary>
/// The <b>simulated</b> firmware image format used by AutoSphere ECU simulators.
/// </summary>
/// <remarks>
/// Layout: <c>"ASFW"</c> magic (4 bytes) · header length (uint16, big-endian) · UTF-8 JSON header · body.
/// The body is deterministic filler data standing in for machine code. A simulated ECU "executes" the
/// image by adopting the version and boot behaviour from the header. This lets the prototype
/// demonstrate a post-install health-check failure (and the resulting rollback) honestly, without
/// pretending to run real ECU software.
/// </remarks>
public static class SimulatedFirmwareImage
{
    private static ReadOnlySpan<byte> Magic => "ASFW"u8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Create(FirmwareImageHeader header, int bodySize = 16 * 1024)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentOutOfRangeException.ThrowIfNegative(bodySize);

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(new HeaderDto(
            header.EcuType, header.Version.ToString(), header.BootBehavior, header.BuildId), JsonOptions);

        var image = new byte[Magic.Length + sizeof(ushort) + headerJson.Length + bodySize];
        Magic.CopyTo(image);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(Magic.Length), checked((ushort)headerJson.Length));
        headerJson.CopyTo(image, Magic.Length + sizeof(ushort));

        // Deterministic filler derived from the build id so identical inputs give identical images.
        var seedHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{header.BuildId}|{header.Version}"));
        var random = new Random(BinaryPrimitives.ReadInt32LittleEndian(seedHash));
        random.NextBytes(image.AsSpan(Magic.Length + sizeof(ushort) + headerJson.Length));
        return image;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> image, out FirmwareImageHeader? header)
    {
        header = null;
        if (image.Length < Magic.Length + sizeof(ushort) || !image[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(image[Magic.Length..]);
        var start = Magic.Length + sizeof(ushort);
        if (image.Length < start + length)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<HeaderDto>(image.Slice(start, length), JsonOptions);
            if (dto is null || !SoftwareVersion.TryParse(dto.Version, out var version))
            {
                return false;
            }

            header = new FirmwareImageHeader(dto.EcuType, version, dto.BootBehavior, dto.BuildId ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Describe(FirmwareImageHeader header) =>
        new StringBuilder()
            .Append(header.EcuType).Append(' ').Append(header.Version)
            .Append(" (boot behaviour: ").Append(header.BootBehavior).Append(')')
            .ToString();

    private sealed record HeaderDto(EcuType EcuType, string Version, FirmwareBootBehavior BootBehavior, string? BuildId);
}
