using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

namespace WritableJsonConfiguration;

public class WritableJsonConfigurationSource : JsonConfigurationSource
{
    public static JsonSerializerOptions DefaultJsonSerializerOptions { get; }

    public ILoggerFactory? LoggerFactory { get; set; }

    public JsonSerializerOptions JsonSerializerOptions { get; set; } = DefaultJsonSerializerOptions;

    static WritableJsonConfigurationSource()
    {
        DefaultJsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IgnoreReadOnlyProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        DefaultJsonSerializerOptions.MakeReadOnly(true);
    }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new WritableJsonConfigurationProvider(this, LoggerFactory?.CreateLogger(nameof(WritableJsonConfigurationProvider)));
    }
}