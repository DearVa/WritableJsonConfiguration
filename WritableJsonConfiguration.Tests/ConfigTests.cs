using Microsoft.Extensions.Configuration;

namespace WritableJsonConfiguration.Tests;

[TestFixture]
public class ConfigTests
{
    private IConfigurationRoot _configuration;

    [SetUp]
    public void SetUp()
    {
        _configuration = WritableJsonConfigurationFabric.Create("Settings.json");
    }

    [TearDown]
    public void TearDown()
    {
        var provider = _configuration.Providers.First() as WritableJsonConfigurationProvider;
        var fileName = provider?.Source.Path;
        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
        {
            try { File.Delete(fileName); } catch { /* ignore */ }
        }
    }

    [Test]
    public void ArrayTest()
    {
        var array = new[] { "1", "a" };
        _configuration.Set("array", array);
        var result = _configuration.Get<string[]>("array");
        Assert.That(result, Is.EqualTo(array));
    }

    [Test]
    public void ArraySectionTest()
    {
        var c = WritableJsonConfigurationFabric.Create("Settings.json");
        var array = new[] { "1", "a" };
        c.GetSection("data").Set("array", array);
        var result = c.GetSection("data").Get<string[]>("array");
        Assert.That(result, Is.EqualTo(array));
    }
}
