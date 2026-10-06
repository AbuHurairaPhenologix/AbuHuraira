using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleSignals.Codec;

namespace AutoSphere.VehicleSignals.Database;

/// <summary>Loads and validates the JSON CAN database.</summary>
public static class CanDatabaseLoader
{
    private const string EmbeddedResourceName = "AutoSphere.VehicleSignals.Database.can-database.json";

    private static readonly Lazy<CanDatabase> DefaultDatabase = new(() =>
    {
        using var stream = typeof(CanDatabaseLoader).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded CAN database '{EmbeddedResourceName}' was not found.");
        return Load(stream);
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The CAN database shipped with AutoSphere (embedded resource).</summary>
    public static CanDatabase Default => DefaultDatabase.Value;

    public static CanDatabase Load(Stream stream)
    {
        var dto = JsonSerializer.Deserialize<DatabaseDto>(stream, JsonOptions)
                  ?? throw new InvalidDataException("CAN database is empty.");
        var messages = dto.Messages.Select(ToDefinition).ToList();
        var database = new CanDatabase(dto.Name, dto.Version, messages);
        Validate(database);
        return database;
    }

    public static CanDatabase LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Checks structural consistency, the equivalent of a DBC lint.</summary>
    public static void Validate(CanDatabase database)
    {
        var errors = new List<string>();
        foreach (var group in database.Messages.GroupBy(m => m.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"CAN id 0x{group.Key:X} is used by {string.Join(", ", group.Select(m => m.Name))}.");
        }

        foreach (var group in database.Signals.GroupBy(s => s.Name).Where(g => g.Count() > 1))
        {
            errors.Add($"Signal name '{group.Key}' is not unique.");
        }

        foreach (var group in database.Signals.GroupBy(s => s.VssPath).Where(g => g.Count() > 1))
        {
            errors.Add($"VSS path '{group.Key}' is mapped by more than one signal.");
        }

        foreach (var message in database.Messages)
        {
            if (message.Length is < 1 or > 64 || message.CycleTimeMs <= 0)
            {
                errors.Add($"{message.Name}: invalid length or cycle time.");
            }

            var occupied = new HashSet<int>();
            if (message.IsE2EProtected)
            {
                for (var bit = 0; bit < 12; bit++)
                {
                    occupied.Add(bit);
                }
            }

            foreach (var signal in message.Signals)
            {
                if (signal.Factor == 0)
                {
                    errors.Add($"{message.Name}.{signal.Name}: factor must not be zero.");
                }

                foreach (var bit in BitCodec.OccupiedBits(signal.StartBit, signal.Length, signal.ByteOrder))
                {
                    if (bit < 0 || bit >= message.Length * 8)
                    {
                        errors.Add($"{message.Name}.{signal.Name}: bit {bit} lies outside the {message.Length}-byte payload.");
                        break;
                    }

                    if (!occupied.Add(bit))
                    {
                        errors.Add($"{message.Name}.{signal.Name}: bit {bit} overlaps another signal or the E2E header.");
                        break;
                    }
                }

                var rawMin = signal.ToRaw(signal.Minimum);
                var rawMax = signal.ToRaw(signal.Maximum);
                if (rawMin < BitCodec.MinRaw(signal.Length, signal.IsSigned) || rawMax > BitCodec.MaxRaw(signal.Length, signal.IsSigned))
                {
                    errors.Add($"{message.Name}.{signal.Name}: physical range [{signal.Minimum}, {signal.Maximum}] does not fit into {signal.Length} bits.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException("Invalid CAN database:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
    }

    private static CanMessageDefinition ToDefinition(MessageDto dto) => new()
    {
        Name = dto.Name,
        Id = ParseHex(dto.Id),
        Length = dto.Length,
        CycleTimeMs = dto.CycleTimeMs,
        Sender = dto.Sender,
        E2EDataId = dto.E2EDataId is null ? null : (ushort)ParseHex(dto.E2EDataId),
        Signals = dto.Signals.Select(s => new CanSignalDefinition
        {
            Name = s.Name,
            VssPath = s.VssPath,
            StartBit = s.StartBit,
            Length = s.Length,
            ByteOrder = s.ByteOrder,
            IsSigned = s.IsSigned,
            Factor = s.Factor ?? 1,
            Offset = s.Offset ?? 0,
            Minimum = s.Minimum,
            Maximum = s.Maximum,
            Unit = s.Unit ?? string.Empty,
            ValueTable = s.ValueTable?.ToDictionary(kv => long.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value)
                         ?? new Dictionary<long, string>(),
        }).ToList(),
    };

    private static uint ParseHex(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(value, CultureInfo.InvariantCulture);

    private sealed record DatabaseDto(string Name, string Version, List<MessageDto> Messages);

    private sealed record MessageDto(string Name, string Id, int Length, int CycleTimeMs, EcuType Sender, string? E2EDataId, List<SignalDto> Signals);

    private sealed record SignalDto(
        string Name,
        string VssPath,
        int StartBit,
        int Length,
        ByteOrder ByteOrder,
        bool IsSigned,
        double? Factor,
        double? Offset,
        double Minimum,
        double Maximum,
        string? Unit,
        Dictionary<string, string>? ValueTable);
}
