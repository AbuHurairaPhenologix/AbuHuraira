namespace AutoSphere.Application.Common;

/// <summary>A required backing service (e.g. the MQTT broker) is unavailable. Mapped to HTTP 503.</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException()
    {
    }

    public ServiceUnavailableException(string message)
        : base(message)
    {
    }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
