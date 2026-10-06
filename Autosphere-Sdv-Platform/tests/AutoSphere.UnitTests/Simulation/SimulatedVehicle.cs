using AutoSphere.CanBus.InMemory;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Diagnostics.Addressing;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.EcuSimulation;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoSphere.UnitTests.Simulation;

/// <summary>A simulated vehicle on an in-memory CAN bus, with fast boot times for tests.</summary>
internal sealed class SimulatedVehicle : IAsyncDisposable
{
    public const string Secret = "unit-test-secret";

    private SimulatedVehicle(InMemoryCanBus bus, VehicleSimulation simulation)
    {
        Bus = bus;
        Simulation = simulation;
    }

    public InMemoryCanBus Bus { get; }

    public VehicleSimulation Simulation { get; }

    public static async Task<SimulatedVehicle> StartAsync(int bootTimeMs = 50)
    {
        var bus = new InMemoryCanBus();
        var simulation = new VehicleSimulation(
            new SimulationOptions { Enabled = true, SecurityAccessSecret = Secret, BootTimeMs = bootTimeMs },
            bus,
            NullLoggerFactory.Instance);
        await simulation.StartAsync(CancellationToken.None);
        var vehicle = new SimulatedVehicle(bus, simulation);
        await vehicle.WaitUntilAsync(() => simulation.Ecus.All(e => e.IsApplicationRunning));
        return vehicle;
    }

    public async Task<UdsClient> CreateTesterAsync(EcuType ecuType)
    {
        var address = DiagnosticAddresses.For(ecuType);
        var channel = await IsoTpChannel.OpenAsync(Bus, $"tester-{ecuType}", address.RequestId, address.ResponseId);
        return new UdsClient(channel, new UdsClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(400) });
    }

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Simulation.DisposeAsync();
        await Bus.DisposeAsync();
    }
}
