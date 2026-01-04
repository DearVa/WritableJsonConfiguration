using System.Reflection;
using WritableJsonConfiguration;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration;

public static class WritableJsonConfigurationProviderExtensions
{
    private static readonly FieldInfo ConfigurationSectionRootFieldInfo =
        typeof(ConfigurationSection).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance) ??
        throw new InvalidOperationException("Could not find _root field in ConfigurationSection");

    /// <param name="configuration"></param>
    extension(IConfiguration configuration)
    {
        /// <summary>
        /// Set value for current section
        /// </summary>
        /// <param name="value"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void Set(object? value)
        {
            switch (configuration)
            {
                case IConfigurationRoot configurationRoot:
                {
                    var provider = configurationRoot.Providers.OfType<WritableJsonConfigurationProvider>().First();
                    provider.Set(null, value);
                    break;
                }
                case ConfigurationSection configurationSection:
                {
                    var root = (IConfigurationRoot)ConfigurationSectionRootFieldInfo.GetValue(configurationSection)!;
                    var provider = root.Providers.OfType<WritableJsonConfigurationProvider>().First();
                    provider.Set(configurationSection.Path, value);
                    break;
                }
                default:
                {
                    throw new NotSupportedException($"Configuration of type {configuration.GetType().FullName} is not supported");
                }
            }
        }

        /// <summary>
        /// Get section and set value
        /// </summary>
        /// <param name="section"></param>
        /// <param name="value"></param>
        public void Set(string section, object? value)
        {
            configuration.GetSection(section).Set(value);
        }

        /// <summary>
        /// Get object by section
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="section"></param>
        /// <returns></returns>
        public T? Get<T>(string section)
        {
            return configuration.GetSection(section).Get<T>();
        }
    }

}