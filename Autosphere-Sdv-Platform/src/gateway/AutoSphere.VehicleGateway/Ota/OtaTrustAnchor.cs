using System.Security.Cryptography;
using AutoSphere.VehicleGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Ota;

/// <summary>
/// The vehicle's trusted OTA signing public key. In a production vehicle this key is provisioned at
/// end-of-line into protected storage; here it is read from a PEM file or configuration value.
/// Loaded lazily so the gateway can start before the backend has generated a development key.
/// </summary>
public sealed class OtaTrustAnchor(IOptions<GatewayOptions> options, ILogger<OtaTrustAnchor> logger) : IDisposable
{
    private readonly object _gate = new();
    private ECDsa? _key;

    public bool TryGetKey(out ECDsa? key, out string? error)
    {
        lock (_gate)
        {
            if (_key is null)
            {
                var gateway = options.Value;
                var pem = gateway.OtaTrustedPublicKeyPem;
                if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(gateway.OtaTrustedPublicKeyPath) && File.Exists(gateway.OtaTrustedPublicKeyPath))
                {
                    pem = File.ReadAllText(gateway.OtaTrustedPublicKeyPath);
                }

                if (string.IsNullOrWhiteSpace(pem))
                {
                    key = null;
                    error = "No trusted OTA public key is provisioned (Gateway:OtaTrustedPublicKeyPath / OtaTrustedPublicKeyPem).";
                    return false;
                }

                var ecdsa = ECDsa.Create();
                try
                {
                    ecdsa.ImportFromPem(pem);
                }
                catch (Exception ex) when (ex is ArgumentException or CryptographicException)
                {
                    ecdsa.Dispose();
                    key = null;
                    error = $"The trusted OTA public key could not be loaded: {ex.Message}";
                    return false;
                }

                _key = ecdsa;
                logger.LogInformation("OTA trust anchor loaded (ECDSA {KeySize}-bit)", ecdsa.KeySize);
            }

            key = _key;
            error = null;
            return true;
        }
    }

    public void Dispose() => _key?.Dispose();
}
