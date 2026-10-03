using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Domain.Entities;

/// <summary>Stored output of a numerical experiment (payload kept as JSON for reproducibility).</summary>
public sealed class ExperimentRecord
{
    private ExperimentRecord()
    {
        Summary = string.Empty;
        ResultJson = "{}";
    }

    public ExperimentRecord(ExperimentKind kind, DateTimeOffset createdAt, double durationMs, string summary, string resultJson)
    {
        Id = Guid.NewGuid();
        Kind = kind;
        CreatedAt = createdAt;
        DurationMs = durationMs;
        Summary = summary;
        ResultJson = resultJson;
    }

    public Guid Id { get; private set; }

    public ExperimentKind Kind { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public double DurationMs { get; private set; }

    public string Summary { get; private set; }

    public string ResultJson { get; private set; }
}
