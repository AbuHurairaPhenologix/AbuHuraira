namespace AnomalyDetection.Domain.Entities;

/// <summary>A monitored service in a specific environment ("Service" in the report ERD, Figure 3.3).</summary>
public sealed class ServiceDefinition
{
    private ServiceDefinition()
    {
    }

    public ServiceDefinition(string name, string environment, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        Name = name;
        Environment = environment;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Environment { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }
}
