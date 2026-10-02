using AutoSphere.Domain.Common;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.Domain.Ota;

/// <summary>A signed ECU software package.</summary>
public sealed class SoftwarePackage
{
    private SoftwarePackage()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public EcuType TargetEcuType { get; private set; }

    public string Version { get; private set; } = string.Empty;

    public string MinimumCompatibleVersion { get; private set; } = string.Empty;

    public byte[] Payload { get; private set; } = [];

    public string PayloadSha256 { get; private set; } = string.Empty;

    public long PayloadSize { get; private set; }

    /// <summary>ECDSA P-256 signature over the canonical manifest (IEEE P1363 format).</summary>
    public byte[] Signature { get; private set; } = [];

    /// <summary>Fingerprint of the signing key (SHA-256 of the public key, first 16 hex characters).</summary>
    public string SigningKeyId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string? ReleaseNotes { get; private set; }

    /// <summary>Description of the (simulated) firmware header, if the payload is a simulated image.</summary>
    public string? FirmwareDescription { get; private set; }

    public SoftwareVersion ParsedVersion => SoftwareVersion.Parse(Version);

    /// <summary>Creates the package manifest; the caller signs it with the platform signing key.</summary>
    public static (SoftwarePackage Package, PackageManifest Manifest) Create(
        string name, EcuType targetEcuType, SoftwareVersion version, SoftwareVersion minimumCompatibleVersion,
        byte[] payload, string createdBy, string? releaseNotes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new DomainException("A software package needs a payload.");
        }

        if (targetEcuType == EcuType.CentralGateway)
        {
            throw new DomainException("Central gateway software cannot be updated through the ECU OTA flow.");
        }

        if (minimumCompatibleVersion > version)
        {
            throw new DomainException("The minimum compatible version cannot be newer than the package version.");
        }

        // Truncate to whole milliseconds so the manifest round-trips identically through every database provider.
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var package = new SoftwarePackage
        {
            Id = Guid.NewGuid(),
            Name = name,
            TargetEcuType = targetEcuType,
            Version = version.ToString(),
            MinimumCompatibleVersion = minimumCompatibleVersion.ToString(),
            Payload = payload,
            PayloadSha256 = PackageIntegrity.ComputeSha256(payload),
            PayloadSize = payload.Length,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            ReleaseNotes = releaseNotes,
            FirmwareDescription = SimulatedFirmwareImage.TryReadHeader(payload, out var header) ? SimulatedFirmwareImage.Describe(header!) : null,
        };
        return (package, package.ToManifest());
    }

    public void ApplySignature(byte[] signature, string signingKeyId)
    {
        if (Signature.Length > 0)
        {
            throw new DomainException("The package is already signed.");
        }

        Signature = signature;
        SigningKeyId = signingKeyId;
    }

    public PackageManifest ToManifest() => new(Id, TargetEcuType, SoftwareVersion.Parse(Version), SoftwareVersion.Parse(MinimumCompatibleVersion),
        PayloadSha256, PayloadSize, CreatedAt);
}
