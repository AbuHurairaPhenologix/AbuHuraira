using System.Security.Cryptography;
using AutoSphere.Application.Abstractions;
using AutoSphere.SharedKernel.Ota;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Infrastructure.Ota;

/// <summary>Configuration section <c>Ota:Signing</c>.</summary>
public sealed class OtaSigningOptions
{
    public const string SectionName = "Ota:Signing";

    /// <summary>PEM file with the ECDSA P-256 private key (PKCS#8). Never commit this file.</summary>
    public string PrivateKeyPath { get; set; } = ".keys/ota-signing-private.pem";

    /// <summary>Where the matching public key is written for vehicles to trust.</summary>
    public string PublicKeyPath { get; set; } = ".keys/ota-signing-public.pem";

    /// <summary>Generate a key pair if none exists (development/demo only).</summary>
    public bool GenerateIfMissing { get; set; }
}

/// <summary>
/// Signs OTA manifests with ECDSA P-256 / SHA-256 using .NET's platform cryptography.
/// </summary>
/// <remarks>
/// Production systems keep the signing key in an HSM or cloud KMS and sign in an offline release
/// pipeline; the prototype loads a PEM file so the complete chain can be demonstrated locally.
/// The private key is never logged or returned by any API.
/// </remarks>
public sealed class EcdsaPackageSigner : IPackageSigner, IDisposable
{
    private readonly ECDsa _key;

    public EcdsaPackageSigner(IOptions<OtaSigningOptions> options, IHostEnvironment environment, ILogger<EcdsaPackageSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var privatePath = Path.GetFullPath(settings.PrivateKeyPath, environment.ContentRootPath);
        var publicPath = Path.GetFullPath(settings.PublicKeyPath, environment.ContentRootPath);
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(privatePath))
        {
            _key.ImportFromPem(File.ReadAllText(privatePath));
        }
        else if (settings.GenerateIfMissing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
            File.WriteAllText(privatePath, _key.ExportPkcs8PrivateKeyPem());
            logger.LogWarning("Generated a new OTA signing key pair at {Directory} (development only; keep this directory out of version control)",
                Path.GetDirectoryName(privatePath));
        }
        else
        {
            throw new InvalidOperationException($"OTA signing key not found at '{privatePath}'. Provide Ota:Signing:PrivateKeyPath or enable GenerateIfMissing for development.");
        }

        PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
        KeyId = Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()))[..16];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
            File.WriteAllText(publicPath, PublicKeyPem);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write the OTA public key to {Path}", publicPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not write the OTA public key to {Path}", publicPath);
        }

        logger.LogInformation("OTA signing key {KeyId} loaded; vehicles must trust {PublicKeyPath}", KeyId, publicPath);
    }

    public string KeyId { get; }

    public string PublicKeyPem { get; }

    public byte[] Sign(PackageManifest manifest) => PackageIntegrity.Sign(manifest, _key);

    public void Dispose() => _key.Dispose();
}
