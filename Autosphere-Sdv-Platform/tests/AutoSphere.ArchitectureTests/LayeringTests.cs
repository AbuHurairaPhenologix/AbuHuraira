using System.Reflection;
using AutoSphere.Application.Abstractions;
using AutoSphere.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AutoSphere.ArchitectureTests;

/// <summary>
/// Enforces the dependency rules of the architecture by inspecting the compiled assemblies, so a layering
/// violation fails the build pipeline instead of being found in a code review.
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly SharedKernel = typeof(SharedKernel.Versioning.SoftwareVersion).Assembly;
    private static readonly Assembly Contracts = typeof(Contracts.Mqtt.MqttTopics).Assembly;
    private static readonly Assembly Domain = typeof(Domain.Vehicles.Vehicle).Assembly;
    private static readonly Assembly ApplicationLayer = typeof(Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;
    private static readonly Assembly CanBus = typeof(CanBus.CanFrame).Assembly;
    private static readonly Assembly VehicleSignals = typeof(VehicleSignals.Codec.CanSignalCodec).Assembly;
    private static readonly Assembly Diagnostics = typeof(Diagnostics.Uds.UdsClient).Assembly;
    private static readonly Assembly EcuSimulation = typeof(EcuSimulation.VehicleSimulation).Assembly;
    private static readonly Assembly Gateway = typeof(VehicleGateway.GatewayServiceCollectionExtensions).Assembly;

    public static TheoryData<string, string[]> ForbiddenDependencies => new()
    {
        { nameof(SharedKernel), ["AutoSphere.", "Microsoft.EntityFrameworkCore", "MQTTnet", "Microsoft.AspNetCore"] },
        { nameof(Contracts), ["AutoSphere.Domain", "AutoSphere.Application", "AutoSphere.Infrastructure", "MQTTnet", "Microsoft.EntityFrameworkCore"] },
        { nameof(Domain), ["AutoSphere.Application", "AutoSphere.Infrastructure", "AutoSphere.Api", "AutoSphere.Contracts", "Microsoft.EntityFrameworkCore", "MQTTnet", "Npgsql", "StackExchange.Redis", "Microsoft.AspNetCore"] },
        { nameof(ApplicationLayer), ["AutoSphere.Infrastructure", "AutoSphere.Api", "Npgsql", "StackExchange.Redis", "MQTTnet", "Microsoft.AspNetCore", "AutoSphere.CanBus", "AutoSphere.VehicleGateway"] },
        { nameof(Infrastructure), ["AutoSphere.Api"] },
        { nameof(CanBus), ["AutoSphere.", "MQTTnet", "Microsoft.EntityFrameworkCore"] },
        { nameof(VehicleSignals), ["AutoSphere.Domain", "AutoSphere.Application", "AutoSphere.Infrastructure", "AutoSphere.Contracts", "MQTTnet"] },
        { nameof(Diagnostics), ["AutoSphere.Domain", "AutoSphere.Application", "AutoSphere.Infrastructure", "AutoSphere.Contracts", "AutoSphere.VehicleSignals", "MQTTnet"] },
        { nameof(EcuSimulation), ["AutoSphere.VehicleGateway", "AutoSphere.Domain", "AutoSphere.Application", "AutoSphere.Infrastructure", "MQTTnet"] },
        { nameof(Gateway), ["AutoSphere.Domain", "AutoSphere.Application", "AutoSphere.Infrastructure", "AutoSphere.Api", "Microsoft.EntityFrameworkCore"] },
    };

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Assembly_does_not_depend_on_forbidden_layers(string assemblyName, string[] forbiddenPrefixes)
    {
        var assembly = Resolve(assemblyName);

        var violations = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(violations.Count == 0, $"{assembly.GetName().Name} must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Controllers_do_not_access_the_database_directly()
    {
        var offenders = Api.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IAutoSphereDbContext) || p.ParameterType == typeof(AutoSphereDbContext)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0, "Business logic belongs in application services; controllers injecting the DbContext: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Domain_entities_do_not_expose_public_setters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace?.StartsWith("AutoSphere.Domain", StringComparison.Ordinal) == true
                        && !IsRecord(t) && !t.Name.EndsWith("Exception", StringComparison.Ordinal) && t.Name != "HealthThresholds")
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod?.IsPublic == true)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Entities must change state through behaviour methods: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Application_services_are_not_named_after_infrastructure()
    {
        var offenders = ApplicationLayer.GetTypes()
            .Where(t => t.Name.Contains("Mqtt", StringComparison.Ordinal) || t.Name.Contains("Redis", StringComparison.Ordinal)
                        || t.Name.Contains("SignalR", StringComparison.Ordinal) || t.Name.Contains("Postgres", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

    private static Assembly Resolve(string name) => name switch
    {
        nameof(SharedKernel) => SharedKernel,
        nameof(Contracts) => Contracts,
        nameof(Domain) => Domain,
        nameof(ApplicationLayer) => ApplicationLayer,
        nameof(Infrastructure) => Infrastructure,
        nameof(CanBus) => CanBus,
        nameof(VehicleSignals) => VehicleSignals,
        nameof(Diagnostics) => Diagnostics,
        nameof(EcuSimulation) => EcuSimulation,
        nameof(Gateway) => Gateway,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
