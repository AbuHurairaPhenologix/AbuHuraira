using Microsoft.EntityFrameworkCore;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Infrastructure.Persistence;

public sealed class SimulationRunRepository : ISimulationRunRepository
{
    private readonly ThermoTwinDbContext _db;

    public SimulationRunRepository(ThermoTwinDbContext db) => _db = db;

    public async Task AddAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        _db.SimulationRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<SimulationRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.SimulationRuns.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SimulationRun>> ListAsync(int take, CancellationToken cancellationToken) =>
        await _db.SimulationRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(cancellationToken);

    public async Task AddSnapshotsAsync(IEnumerable<SimulationSnapshot> snapshots, CancellationToken cancellationToken)
    {
        _db.SimulationSnapshots.AddRange(snapshots);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SimulationSnapshot>> GetSnapshotsAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.SimulationSnapshots.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Time).ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}

public sealed class ExperimentRepository : IExperimentRepository
{
    private readonly ThermoTwinDbContext _db;

    public ExperimentRepository(ThermoTwinDbContext db) => _db = db;

    public async Task AddAsync(ExperimentRecord record, CancellationToken cancellationToken)
    {
        _db.Experiments.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<ExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Experiments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<ExperimentRecord?> GetLatestAsync(ExperimentKind kind, CancellationToken cancellationToken) =>
        _db.Experiments.AsNoTracking().Where(e => e.Kind == kind).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ExperimentRecord>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Experiments.AsNoTracking().OrderByDescending(e => e.CreatedAt).ToListAsync(cancellationToken);

    public Task<bool> AnyAsync(ExperimentKind kind, CancellationToken cancellationToken) =>
        _db.Experiments.AnyAsync(e => e.Kind == kind, cancellationToken);
}
