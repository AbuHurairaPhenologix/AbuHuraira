namespace AutoSphere.Contracts.Mqtt;

/// <summary>Connection settings for the MQTT broker (configuration section <c>Mqtt</c>).</summary>
public sealed class MqttBrokerOptions
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1883;

    /// <summary>Client id prefix; a unique suffix is appended per process.</summary>
    public string ClientId { get; set; } = "autosphere";

    /// <summary>Optional credentials. Supply through environment variables, never commit them.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool UseTls { get; set; }

    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>Initial delay between reconnection attempts; doubled up to <see cref="MaxReconnectDelaySeconds"/>.</summary>
    public int ReconnectDelaySeconds { get; set; } = 1;

    public int MaxReconnectDelaySeconds { get; set; } = 15;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
