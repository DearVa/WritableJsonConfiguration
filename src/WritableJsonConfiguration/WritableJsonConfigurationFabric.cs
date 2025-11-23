using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WritableJsonConfiguration;

public static class WritableJsonConfigurationFabric
{
    public static IConfigurationRoot Create(string path, bool reloadOnChange = true, bool optional = true, ILoggerFactory? loggerFactory = null)
    {
        void ConfigureSource(WritableJsonConfigurationSource s)
        {
            s.Path = path;
            s.Optional = optional;
            s.ReloadOnChange = reloadOnChange;
            s.LoggerFactory = loggerFactory;
            s.ResolveFileProvider();
        }

        return Create(ConfigureSource);
    }

    public static IConfigurationRoot Create(Action<WritableJsonConfigurationSource> configureSource)
    {
        var configurationBuilder = new ConfigurationBuilder();
        var configuration = configurationBuilder.Add(configureSource).Build();
        return configuration;
    }
}