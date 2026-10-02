using System.Globalization;
using System.Text;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.SharedKernel.Ota;

/// <summary>
/// Metadata that describes an OTA software package. The manifest — not the payload — is signed;
/// the payload is bound to the signature through <see cref="PayloadSha256"/>.
/// </summary>
public sealed record PackageManifest(
    Guid PackageId,
    EcuType TargetEcuType,
    SoftwareVersion Version,
    SoftwareVersion MinimumCompatibleVersion,
    string PayloadSha256,
    long PayloadSize,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Produces the canonical byte representation that is signed and verified.
    /// A fixed line-based format is used instead of JSON so that whitespace, property order or
    /// serializer settings can never change the signed bytes.
    /// </summary>
    public byte[] ToCanonicalBytes()
    {
        var builder = new StringBuilder();
        builder.Append("autosphere-ota-manifest/v1\n");
        builder.Append("packageId=").Append(PackageId.ToString("D")).Append('\n');
        builder.Append("targetEcuType=").Append(TargetEcuType.ToString()).Append('\n');
        builder.Append("version=").Append(Version).Append('\n');
        builder.Append("minimumCompatibleVersion=").Append(MinimumCompatibleVersion).Append('\n');
        builder.Append("payloadSha256=").Append(PayloadSha256.ToLowerInvariant()).Append('\n');
        builder.Append("payloadSize=").Append(PayloadSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("createdAt=").Append(CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
