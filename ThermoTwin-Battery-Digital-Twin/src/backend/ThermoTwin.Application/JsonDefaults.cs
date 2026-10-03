using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThermoTwin.Application;

/// <summary>Serializer settings shared by persistence, the API and the experiment exporter.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Apply(options);
        return options;
    }
}
