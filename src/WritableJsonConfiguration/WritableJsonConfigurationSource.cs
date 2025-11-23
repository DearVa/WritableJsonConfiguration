using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

namespace WritableJsonConfiguration;

public class WritableJsonConfigurationSource : JsonConfigurationSource
{
    public ILoggerFactory? LoggerFactory { get; set; }

    public JsonSerializerOptions JsonSerializerOptions { get; set; } =
        new()
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IgnoreReadOnlyProperties = true
        };

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new WritableJsonConfigurationProvider(this, LoggerFactory?.CreateLogger(nameof(WritableJsonConfigurationProvider)));
    }
}