using System.Security.Cryptography;
using AutoSphere.CanBus;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using BenchmarkDotNet.Attributes;

namespace AutoSphere.Benchmarks;

/// <summary>Cost of the gateway's per-frame work: CAN decode (incl. E2E check) and encode.</summary>
[MemoryDiagnoser]
public class SignalCodecBenchmarks
{
    private readonly CanSignalCodec _codec = new(CanDatabaseLoader.Default);
    private CanFrame _bmsStatus = null!;
    private CanFrame _bmsPackMotorola = null!;
    private CanMessageDefinition _vcuStatus = null!;
    private readonly Dictionary<string, double> _vcuValues = new() { ["VehicleSpeed"] = 72, ["GearPosition"] = 3, ["AcceleratorPedalPosition"] = 12.4, ["IgnitionStatus"] = 2 };

    [GlobalSetup]
    public void Setup()
    {
        var database = CanDatabaseLoader.Default;
        _vcuStatus = database.GetMessage("VCU_Status");
        _bmsStatus = new CanFrame(0x300, _codec.Encode(database.GetMessage("BMS_Status"),
            new Dictionary<string, double> { ["BatteryStateOfCharge"] = 78.4, ["BatteryTemperature"] = 38.1 }, 1), DateTimeOffset.UtcNow);
        _bmsPackMotorola = new CanFrame(0x301, _codec.Encode(database.GetMessage("BMS_Pack"),
            new Dictionary<string, double> { ["BatteryVoltage"] = 399.5, ["BatteryCurrent"] = 21.3 }, 1), DateTimeOffset.UtcNow);
    }

    [Benchmark(Baseline = true)]
    public bool DecodeIntelMessage() => _codec.TryDecode(_bmsStatus, out _);

    [Benchmark]
    public bool DecodeMotorolaMessage() => _codec.TryDecode(_bmsPackMotorola, out _);

    [Benchmark]
    public byte[] EncodeMessage() => _codec.Encode(_vcuStatus, _vcuValues, 5);

    [Benchmark]
    public byte E2ECrc8() => E2EProtection.ComputeCrc(0x0300, _bmsStatus.Data.Span);
}

/// <summary>Vehicle-side OTA verification cost as a function of package size.</summary>
[MemoryDiagnoser]
public class OtaVerificationBenchmarks
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private byte[] _payload = [];
    private byte[] _signature = [];
    private PackageManifest _manifest = null!;
    private OtaVerificationContext _context = null!;

    [Params(16 * 1024, 256 * 1024, 1024 * 1024)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = RandomNumberGenerator.GetBytes(PayloadBytes);
        _manifest = new PackageManifest(Guid.NewGuid(), EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"), SoftwareVersion.Parse("1.0.0"),
            PackageIntegrity.ComputeSha256(_payload), _payload.Length, DateTimeOffset.UtcNow);
        _signature = PackageIntegrity.Sign(_manifest, _key);
        _context = new OtaVerificationContext(EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.0.0"), _key);
    }

    [Benchmark]
    public bool VerifyPackage() => OtaPackageVerifier.Verify(_manifest, _payload, _signature, _context).IsValid;

    [GlobalCleanup]
    public void Cleanup() => _key.Dispose();
}
