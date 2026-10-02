using System.Globalization;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.VehicleSignals.Database;

public enum ByteOrder
{
    /// <summary>Little-endian; start bit is the LSB (DBC <c>@1</c>).</summary>
    Intel = 0,

    /// <summary>Big-endian; start bit is the MSB in DBC sawtooth numbering (DBC <c>@0</c>).</summary>
    Motorola = 1,
}

/// <summary>Definition of one signal inside a CAN message (equivalent to a DBC <c>SG_</c> line).</summary>
public sealed class CanSignalDefinition
{
    public required string Name { get; init; }

    /// <summary>VSS-inspired normalized path.</summary>
    public required string VssPath { get; init; }

    public required int StartBit { get; init; }

    public required int Length { get; init; }

    public ByteOrder ByteOrder { get; init; }

    public bool IsSigned { get; init; }

    public double Factor { get; init; } = 1;

    public double Offset { get; init; }

    public double Minimum { get; init; }

    public double Maximum { get; init; }

    public string Unit { get; init; } = string.Empty;

    /// <summary>Optional enumeration labels keyed by raw value (DBC <c>VAL_</c>).</summary>
    public IReadOnlyDictionary<long, string> ValueTable { get; init; } = new Dictionary<long, string>();

    public double ToPhysical(long raw) => (raw * Factor) + Offset;

    public long ToRaw(double physical) => (long)Math.Round((physical - Offset) / Factor, MidpointRounding.AwayFromZero);

    public bool IsInRange(double physical) => physical >= Minimum - (Factor / 2) && physical <= Maximum + (Factor / 2);

    public string? LabelFor(long raw) => ValueTable.TryGetValue(raw, out var label) ? label : null;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({VssPath}) {StartBit}|{Length}@{(ByteOrder == ByteOrder.Intel ? 1 : 0)}{(IsSigned ? '-' : '+')} ({Factor},{Offset}) [{Minimum}|{Maximum}] \"{Unit}\"");
}

/// <summary>Definition of one CAN message (equivalent to a DBC <c>BO_</c> block).</summary>
public sealed class CanMessageDefinition
{
    public required string Name { get; init; }

    public required uint Id { get; init; }

    public required int Length { get; init; }

    /// <summary>Nominal transmission cycle; drives timeout supervision at the receiver.</summary>
    public required int CycleTimeMs { get; init; }

    public required EcuType Sender { get; init; }

    /// <summary>Data id mixed into the E2E CRC; <c>null</c> disables E2E protection for the message.</summary>
    public ushort? E2EDataId { get; init; }

    public required IReadOnlyList<CanSignalDefinition> Signals { get; init; }

    public bool IsE2EProtected => E2EDataId.HasValue;
}

/// <summary>The complete set of CAN messages of a vehicle (equivalent to a DBC file).</summary>
public sealed class CanDatabase
{
    private readonly Dictionary<uint, CanMessageDefinition> _byId;
    private readonly Dictionary<string, CanMessageDefinition> _byName;
    private readonly Dictionary<string, (CanMessageDefinition Message, CanSignalDefinition Signal)> _signalsByName;

    public CanDatabase(string name, string version, IReadOnlyList<CanMessageDefinition> messages)
    {
        Name = name;
        Version = version;
        Messages = messages;
        _byId = messages.ToDictionary(m => m.Id);
        _byName = messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        _signalsByName = messages.SelectMany(m => m.Signals.Select(s => (m, s))).ToDictionary(x => x.s.Name, x => (x.m, x.s), StringComparer.Ordinal);
    }

    public string Name { get; }

    public string Version { get; }

    public IReadOnlyList<CanMessageDefinition> Messages { get; }

    public IEnumerable<CanSignalDefinition> Signals => Messages.SelectMany(m => m.Signals);

    public bool TryGetMessage(uint id, out CanMessageDefinition message) => _byId.TryGetValue(id, out message!);

    public CanMessageDefinition GetMessage(string name) =>
        _byName.TryGetValue(name, out var message) ? message : throw new KeyNotFoundException($"CAN message '{name}' is not defined.");

    public (CanMessageDefinition Message, CanSignalDefinition Signal) GetSignal(string name) =>
        _signalsByName.TryGetValue(name, out var entry) ? entry : throw new KeyNotFoundException($"CAN signal '{name}' is not defined.");

    public IEnumerable<CanMessageDefinition> MessagesSentBy(EcuType sender) => Messages.Where(m => m.Sender == sender);
}
