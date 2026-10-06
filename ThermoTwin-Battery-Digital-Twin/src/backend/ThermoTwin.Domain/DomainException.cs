namespace ThermoTwin.Domain;

/// <summary>Violation of a domain rule (mapped to HTTP 409 by the API).</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException()
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
