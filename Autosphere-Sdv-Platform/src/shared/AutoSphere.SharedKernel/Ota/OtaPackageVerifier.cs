using System.Security.Cryptography;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.SharedKernel.Ota;

public enum OtaVerificationCheck
{
    PayloadSize,
    Checksum,
    Signature,
    TargetEcu,
    VersionCompatibility,
    Downgrade,
}

public sealed record OtaVerificationFailure(OtaVerificationCheck Check, string Message);

public sealed record OtaVerificationResult(IReadOnlyList<OtaVerificationCheck> Passed, IReadOnlyList<OtaVerificationFailure> Failures)
{
    public bool IsValid => Failures.Count == 0;

    public string Summary => IsValid
        ? "Package verified: " + string.Join(", ", Passed)
        : string.Join("; ", Failures.Select(f => $"{f.Check}: {f.Message}"));
}

/// <summary>Everything the vehicle knows when it verifies a received package.</summary>
public sealed record OtaVerificationContext(EcuType InstalledEcuType, SoftwareVersion InstalledVersion, ECDsa TrustedPublicKey);

/// <summary>
/// Vehicle-side verification of an OTA package before installation. All checks are executed and all
/// failures reported, so that an engineer sees every problem at once, but installation is only allowed
/// when <see cref="OtaVerificationResult.IsValid"/> is true.
/// </summary>
public static class OtaPackageVerifier
{
    public static OtaVerificationResult Verify(
        PackageManifest manifest,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature,
        OtaVerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);

        var passed = new List<OtaVerificationCheck>();
        var failures = new List<OtaVerificationFailure>();

        void Check(OtaVerificationCheck check, bool ok, string failureMessage)
        {
            if (ok)
            {
                passed.Add(check);
            }
            else
            {
                failures.Add(new OtaVerificationFailure(check, failureMessage));
            }
        }

        // 1. + 2. Integrity: the bytes that arrived are the bytes described by the manifest.
        Check(OtaVerificationCheck.PayloadSize, payload.Length == manifest.PayloadSize,
            $"payload is {payload.Length} bytes, manifest declares {manifest.PayloadSize} bytes");
        Check(OtaVerificationCheck.Checksum, PackageIntegrity.ChecksumMatches(payload, manifest.PayloadSha256),
            "SHA-256 of the received payload does not match the manifest");

        // 3. Authenticity: the manifest was produced by the trusted signing authority.
        Check(OtaVerificationCheck.Signature, PackageIntegrity.VerifySignature(manifest, signature, context.TrustedPublicKey),
            "ECDSA P-256 signature over the manifest is invalid");

        // 4. Applicability.
        Check(OtaVerificationCheck.TargetEcu, manifest.TargetEcuType == context.InstalledEcuType,
            $"package targets {manifest.TargetEcuType}, ECU is {context.InstalledEcuType}");
        Check(OtaVerificationCheck.VersionCompatibility, context.InstalledVersion >= manifest.MinimumCompatibleVersion,
            $"installed version {context.InstalledVersion} is below the minimum compatible version {manifest.MinimumCompatibleVersion}");
        Check(OtaVerificationCheck.Downgrade, manifest.Version > context.InstalledVersion,
            $"package version {manifest.Version} is not newer than installed version {context.InstalledVersion}");

        return new OtaVerificationResult(passed, failures);
    }
}
