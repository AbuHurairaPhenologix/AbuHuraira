using System.Security.Cryptography;
using System.Text;

namespace AutoSphere.Diagnostics.Security;

/// <summary>
/// Seed/key computation for SecurityAccess (0x27).
/// </summary>
/// <remarks>
/// Real OEM algorithms are confidential. AutoSphere uses HMAC-SHA256 with a shared secret and truncates
/// the MAC to 4 bytes — a standard primitive, deliberately not a home-made cipher. The secret comes from
/// configuration and is never logged.
/// </remarks>
public static class SecurityAccessAlgorithm
{
    public const int SeedLength = 4;
    public const int KeyLength = 4;

    public static byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedLength);

    public static byte[] ComputeKey(ReadOnlySpan<byte> seed, string sharedSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(sharedSecret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), seed);
        return mac[..KeyLength];
    }

    public static bool KeyIsValid(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> key, string sharedSecret) =>
        key.Length == KeyLength && CryptographicOperations.FixedTimeEquals(ComputeKey(seed, sharedSecret), key);
}
