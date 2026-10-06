using System.Security.Cryptography;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Ota;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Ota;

public sealed class CreatePackageValidator : AbstractValidator<CreatePackageRequest>
{
    public CreatePackageValidator(IOptions<OtaOptions> options)
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(128);
        RuleFor(r => r.TargetEcuType).IsInEnum().NotEqual(EcuType.CentralGateway);
        RuleFor(r => r.Version).Must(v => SoftwareVersion.TryParse(v, out _)).WithMessage("Version must have the form MAJOR.MINOR.PATCH.");
        RuleFor(r => r.MinimumCompatibleVersion).Must(v => SoftwareVersion.TryParse(v, out _)).WithMessage("Minimum version must have the form MAJOR.MINOR.PATCH.");
        RuleFor(r => r.Payload).NotEmpty();
        RuleFor(r => r.Payload.Length).LessThanOrEqualTo(options.Value.MaxPackageSizeBytes).When(r => r.Payload is not null)
            .WithMessage($"Packages are limited to {options.Value.MaxPackageSizeBytes} bytes.");
        RuleFor(r => r.ReleaseNotes).MaximumLength(2000);
    }
}

public sealed class CreateSamplePackageValidator : AbstractValidator<CreateSamplePackageRequest>
{
    public CreateSamplePackageValidator()
    {
        RuleFor(r => r.TargetEcuType).IsInEnum().NotEqual(EcuType.CentralGateway);
        RuleFor(r => r.Version).Must(v => SoftwareVersion.TryParse(v, out _)).WithMessage("Version must have the form MAJOR.MINOR.PATCH.");
        RuleFor(r => r.MinimumCompatibleVersion).Must(v => SoftwareVersion.TryParse(v, out _)).WithMessage("Minimum version must have the form MAJOR.MINOR.PATCH.");
        RuleFor(r => r.BootBehavior).IsInEnum();
        RuleFor(r => r.SizeBytes).InclusiveBetween(256, 512 * 1024);
    }
}

/// <summary>Creation and signing of OTA software packages.</summary>
public sealed class SoftwarePackageService(
    IAutoSphereDbContext db,
    IPackageSigner signer,
    ICurrentUser currentUser,
    IValidator<CreatePackageRequest> validator,
    IValidator<CreateSamplePackageRequest> sampleValidator,
    TimeProvider timeProvider,
    ILogger<SoftwarePackageService> logger)
{
    public async Task<SoftwarePackageDto> CreateAsync(CreatePackageRequest request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var version = SoftwareVersion.Parse(request.Version);
        if (await db.SoftwarePackages.AnyAsync(p => p.TargetEcuType == request.TargetEcuType && p.Version == version.ToString(), cancellationToken))
        {
            throw new DomainException($"A {request.TargetEcuType} package with version {version} already exists.");
        }

        var (package, manifest) = SoftwarePackage.Create(request.Name, request.TargetEcuType, version, SoftwareVersion.Parse(request.MinimumCompatibleVersion),
            request.Payload, currentUser.UserName, request.ReleaseNotes, timeProvider.GetUtcNow());
        package.ApplySignature(signer.Sign(manifest), signer.KeyId);
        db.SoftwarePackages.Add(package);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Software package {PackageId} {EcuType} {Version} created and signed with key {KeyId} (SHA-256 {Sha256})",
            package.Id, package.TargetEcuType, package.Version, signer.KeyId, package.PayloadSha256);
        return package.ToDto();
    }

    /// <summary>Generates and signs a <b>simulated</b> firmware image for the ECU simulators.</summary>
    public async Task<SoftwarePackageDto> CreateSampleAsync(CreateSamplePackageRequest request, CancellationToken cancellationToken)
    {
        await sampleValidator.ValidateAndThrowAsync(request, cancellationToken);
        var version = SoftwareVersion.Parse(request.Version);
        var buildId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var image = SimulatedFirmwareImage.Create(new FirmwareImageHeader(request.TargetEcuType, version, request.BootBehavior, buildId), request.SizeBytes);
        var notes = request.ReleaseNotes ?? (request.BootBehavior == FirmwareBootBehavior.Normal
            ? "Simulated firmware image."
            : $"Simulated firmware image that exhibits '{request.BootBehavior}' after boot (demonstrates health-check failure and rollback).");
        return await CreateAsync(new CreatePackageRequest($"{request.TargetEcuType} {version} (simulated)", request.TargetEcuType, request.Version,
            request.MinimumCompatibleVersion, notes, image), cancellationToken);
    }

    public async Task<IReadOnlyList<SoftwarePackageDto>> ListAsync(CancellationToken cancellationToken)
    {
        var packages = await db.SoftwarePackages.AsNoTracking().OrderBy(p => p.TargetEcuType).ThenByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
        return packages.Select(p => p.ToDto()).ToList();
    }

    public async Task<SoftwarePackageDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.SoftwarePackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw NotFoundException.For("Software package", id)).ToDto();

    public string TrustedPublicKeyPem => signer.PublicKeyPem;
}
