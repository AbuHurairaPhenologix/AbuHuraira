using System.Security.Cryptography;

namespace AutoSphere.SharedKernel.Ota;

/// <summary>
/// Integrity and authenticity primitives for OTA packages, built exclusively on .NET's standard
/// cryptography (SHA-256 and ECDSA over NIST P-256). No custom cryptography is implemented.
/// </summary>
public static class PackageIntegrity
{
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    /// <summary>Computes the lower-case hexadecimal SHA-256 digest of the payload.</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>Compares the payload digest with the expected value in constant time.</summary>
    public static bool ChecksumMatches(ReadOnlySpan<byte> payload, string expectedSha256Hex)
    {
        if (string.IsNullOrEmpty(expectedSha256Hex) || expectedSha256Hex.Length != 64)
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256Hex);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Signs the canonical manifest with an ECDSA private key (IEEE P1363 signature format).</summary>
    public static byte[] Sign(PackageManifest manifest, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(privateKey);
        return privateKey.SignData(manifest.ToCanonicalBytes(), HashAlgorithm);
    }

    /// <summary>Verifies the manifest signature with the trusted ECDSA public key.</summary>
    public static bool VerifySignature(PackageManifest manifest, ReadOnlySpan<byte> signature, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publicKey);
        try
        {
            return publicKey.VerifyData(manifest.ToCanonicalBytes(), signature, HashAlgorithm);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
