using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

namespace WritableJsonConfiguration;

public class WritableJsonConfigurationSource : JsonConfigurationSource
{
    public static JsonSerializerOptions DefaultJsonSerializerOptions { get; } =
        new()
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IgnoreReadOnlyProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

    public ILoggerFactory? LoggerFactory { get; set; }

    public JsonSerializerOptions JsonSerializerOptions { get; set; } = DefaultJsonSerializerOptions;

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new WritableJsonConfigurationProvider(this, LoggerFactory?.CreateLogger(nameof(WritableJsonConfigurationProvider)));
    }
}