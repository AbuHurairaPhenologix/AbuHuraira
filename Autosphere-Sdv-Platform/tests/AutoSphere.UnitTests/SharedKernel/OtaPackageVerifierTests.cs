using System.Security.Cryptography;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.UnitTests.SharedKernel;

public sealed class OtaPackageVerifierTests : IDisposable
{
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _trustedKey = ECDsa.Create();
    private readonly byte[] _payload;
    private readonly PackageManifest _manifest;
    private readonly byte[] _signature;

    public OtaPackageVerifierTests()
    {
        _trustedKey.ImportSubjectPublicKeyInfo(_signingKey.ExportSubjectPublicKeyInfo(), out _);
        _payload = SimulatedFirmwareImage.Create(new FirmwareImageHeader(EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"), FirmwareBootBehavior.Normal, "b1"), 2048);
        _manifest = new PackageManifest(Guid.NewGuid(), EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"), SoftwareVersion.Parse("1.0.0"),
            PackageIntegrity.ComputeSha256(_payload), _payload.Length, DateTimeOffset.UtcNow);
        _signature = PackageIntegrity.Sign(_manifest, _signingKey);
    }

    [Fact]
    public void Valid_package_passes_every_check()
    {
        var result = Verify(_manifest, _payload, _signature, "1.0.0");

        Assert.True(result.IsValid);
        Assert.Equal(6, result.Passed.Count);
    }

    [Fact]
    public void Corrupted_payload_fails_checksum()
    {
        var corrupted = (byte[])_payload.Clone();
        corrupted[100] ^= 0xFF;

        var result = Verify(_manifest, corrupted, _signature, "1.0.0");

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f => f.Check == OtaVerificationCheck.Checksum);
        Assert.DoesNotContain(result.Failures, f => f.Check == OtaVerificationCheck.Signature);
    }

    [Fact]
    public void Tampered_manifest_fails_signature()
    {
        var tampered = _manifest with { MinimumCompatibleVersion = SoftwareVersion.Parse("0.1.0") };

        var result = Verify(tampered, _payload, _signature, "1.0.0");

        Assert.Contains(result.Failures, f => f.Check == OtaVerificationCheck.Signature);
    }

    [Fact]
    public void Package_signed_by_untrusted_key_fails_signature()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = Verify(_manifest, _payload, PackageIntegrity.Sign(_manifest, attacker), "1.0.0");

        Assert.Contains(result.Failures, f => f.Check == OtaVerificationCheck.Signature);
    }

    [Theory]
    [InlineData("1.1.0", OtaVerificationCheck.Downgrade)]
    [InlineData("1.2.0", OtaVerificationCheck.Downgrade)]
    [InlineData("0.9.0", OtaVerificationCheck.VersionCompatibility)]
    public void Version_rules_are_enforced(string installed, OtaVerificationCheck expectedFailure)
    {
        var result = Verify(_manifest, _payload, _signature, installed);

        Assert.Equal(expectedFailure, Assert.Single(result.Failures).Check);
    }

    [Fact]
    public void Package_for_another_ecu_type_is_rejected()
    {
        var result = OtaPackageVerifier.Verify(_manifest, _payload, _signature,
            new OtaVerificationContext(EcuType.MotorControlUnit, SoftwareVersion.Parse("1.0.0"), _trustedKey));

        Assert.Equal(OtaVerificationCheck.TargetEcu, Assert.Single(result.Failures).Check);
    }

    [Fact]
    public void Canonical_manifest_bytes_are_stable()
    {
        var copy = _manifest with { PayloadSha256 = _manifest.PayloadSha256.ToUpperInvariant() };

        Assert.Equal(_manifest.ToCanonicalBytes(), copy.ToCanonicalBytes());
    }

    [Fact]
    public void Simulated_firmware_header_round_trips()
    {
        Assert.True(SimulatedFirmwareImage.TryReadHeader(_payload, out var header));
        Assert.Equal(EcuType.BatteryManagementSystem, header!.EcuType);
        Assert.Equal("1.1.0", header.Version.ToString());
        Assert.False(SimulatedFirmwareImage.TryReadHeader(new byte[] { 1, 2, 3, 4, 5, 6 }, out _));
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        _trustedKey.Dispose();
    }

    private OtaVerificationResult Verify(PackageManifest manifest, byte[] payload, byte[] signature, string installed) =>
        OtaPackageVerifier.Verify(manifest, payload, signature,
            new OtaVerificationContext(EcuType.BatteryManagementSystem, SoftwareVersion.Parse(installed), _trustedKey));
}
