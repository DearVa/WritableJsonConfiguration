using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace WritableJsonConfiguration.Tests;

public abstract class ProviderTestBase : IDisposable
{
    protected readonly string _tempDir;
    protected readonly string _filePath;

    protected ProviderTestBase(string? initialJson = null)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WritableCfgTests_" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(_filePath, initialJson ?? "{}");
    }

    protected WritableJsonConfigurationProvider CreateProvider(ILogger? logger = null)
    {
        var source = new WritableJsonConfigurationSource
        {
            Path = Path.GetFileName(_filePath),
            Optional = false,
            ReloadOnChange = false,
            FileProvider = new PhysicalFileProvider(_tempDir),
            JsonSerializerOptions =
            {
                IgnoreReadOnlyProperties = false,
            },
        };
        source.ResolveFileProvider();
        var provider = new WritableJsonConfigurationProvider(source, logger);
        using var fs = File.OpenRead(_filePath);
        provider.Load(fs);
        return provider;
    }

    protected string ReadFile() => File.ReadAllText(_filePath);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}

[TestFixture]
public sealed class WritableJsonConfigurationProviderTests() : ProviderTestBase(
    """
    {
        "Numbers":[1,2,3,4],
        "Obj":{"A":"1","B":"2","C":"3"}
    }
    """)
{

    [Test]
    public async Task LeafSet_DoesNotRemoveSiblingKeys()
    {
        var p = CreateProvider();
        p.Set("Obj:A", "100");
        await p.FlushAsync();
        var obj = GetJson().GetProperty("Obj");
        Assert.Multiple(() =>
        {
            Assert.That(obj.GetProperty("A").GetString(), Is.EqualTo("100"));
            Assert.That(obj.GetProperty("B").GetString(), Is.EqualTo("2"));
            Assert.That(obj.GetProperty("C").GetString(), Is.EqualTo("3"));
        });
    }

    [Test]
    public async Task ObjectSubtreeReplacement_RemovesMissingKeys()
    {
        var p = CreateProvider();
        p.Set("Obj", new { A = "10", B = "20" });
        await p.FlushAsync();
        var obj = GetJson().GetProperty("Obj");
        Assert.Multiple(() =>
        {
            Assert.That(obj.TryGetProperty("A", out _), Is.True);
            Assert.That(obj.TryGetProperty("B", out _), Is.True);
            Assert.That(obj.TryGetProperty("C", out _), Is.False);
        });
    }

    [Test]
    public async Task ArrayShrink_RemovesStaleIndices()
    {
        var p = CreateProvider();
        p.Set("Numbers", new[] { 9, 8 });
        await p.FlushAsync();
        var arr = GetJson().GetProperty("Numbers");
        Assert.Multiple(() =>
        {
            Assert.That(arr.GetArrayLength(), Is.EqualTo(2));
            Assert.That(arr[0].GetInt32(), Is.EqualTo(9));
            Assert.That(arr[1].GetInt32(), Is.EqualTo(8));
            Assert.That(p.TryGet("Numbers:2", out _), Is.False);
        });
    }

    [Test]
    public async Task ArrayExpand_AddsNewIndices()
    {
        var p = CreateProvider();
        p.Set("Numbers", new[] { 5, 6, 7, 8, 9, 10 });
        await p.FlushAsync();
        var arr = GetJson().GetProperty("Numbers");
        Assert.Multiple(() =>
        {
            Assert.That(arr.GetArrayLength(), Is.EqualTo(6));
            Assert.That(arr[5].GetInt32(), Is.EqualTo(10));
        });
    }

    [Test]
    public async Task RootReplacement_ResetsContent()
    {
        var p = CreateProvider();
        p.Set(null, new { RootOnly = 1 });
        await p.FlushAsync();
        var j = GetJson();
        Assert.Multiple(() =>
        {
            Assert.That(j.TryGetProperty("RootOnly", out _), Is.True);
            Assert.That(j.TryGetProperty("Numbers", out _), Is.False);
        });
    }

    [Test]
    public async Task RootReplacement_WithNull_CreatesEmptyObject()
    {
        var p = CreateProvider();
        p.Set(null, null);
        await p.FlushAsync();
        var text = ReadFile().Trim().Replace("\r", "");
        Assert.That(text, Is.EqualTo("{}"));
    }

    [Test]
    public async Task ConcurrentWrites_ArrayIntegrityMaintained()
    {
        var p = CreateProvider();
        Parallel.For(
            0,
            50,
            i =>
            {
                if (i % 2 == 0)
                    p.Set("Numbers", Enumerable.Range(0, 3 + (i % 5)).ToArray());
                else
                    p.Set("Obj:A", i.ToString());
            });
        await p.FlushAsync();
        var json = GetJson();
        var numbers = json.GetProperty("Numbers");
        var len = numbers.GetArrayLength();
        for (var idx = len; idx < len + 3; idx++)
            Assert.That(p.TryGet($"Numbers:{idx}", out _), Is.False);
    }

    [Test]
    public async Task DebouncedSave_WritesAfterDelay()
    {
        var p = CreateProvider();
        p.Set("Obj:A", "X1");
        await Task.Delay(350);
        var text = ReadFile();
        Assert.That(text, Does.Contain("\"A\": \"X1\""));
    }

    [Test]
    public async Task Flush_ImmediatePersistence()
    {
        var p = CreateProvider();
        p.Set("Obj:A", "ZZ");
        await p.FlushAsync();
        var text = ReadFile();
        Assert.That(text, Does.Contain("\"A\": \"ZZ\""));
    }

    [Test]
    public async Task WriteError_EventRaised()
    {
        var logger = new TestLogger();
        var p = CreateProvider(logger);

        // Force a write failure by making the target file read-only
        var originalAttributes = File.GetAttributes(_filePath);
        File.SetAttributes(_filePath, originalAttributes | FileAttributes.ReadOnly);

        try
        {
            p.Set("Obj:A", "LOCKED");

            // Flush may throw or not depending on implementation; we only care that event fired.
            try { await p.FlushAsync(); }
            catch
            { /* ignore */
            }

            // If implementation debounces without Flush forcing, add a small wait (usually unnecessary).
            // Task.Delay(50).GetAwaiter().GetResult();

            Assert.That(logger.ErrorCount, Is.GreaterThan(0), "No error log entries were recorded.");
        }
        finally
        {
            // Restore attributes so temp directory cleanup succeeds
            File.SetAttributes(_filePath, originalAttributes);
        }
    }

    [Test]
    public async Task EmptyArrayAndObject_FlattenedAsNullEntries()
    {
        var p = CreateProvider();
        p.Set("Meta", new { EmptyArr = Array.Empty<int>(), EmptyObj = new { } });
        await p.FlushAsync();
        Assert.Multiple(() =>
        {
            Assert.That(p.TryGet("Meta:EmptyArr", out var v1), Is.True);
            Assert.That(v1, Is.Null);
            Assert.That(p.TryGet("Meta:EmptyObj", out var v2), Is.True);
            Assert.That(v2, Is.Null);
        });
    }

    private JsonElement GetJson()
    {
        using var doc = JsonDocument.Parse(ReadFile());
        return doc.RootElement.Clone();
    }

    private sealed class TestLogger : ILogger
    {
        public int ErrorCount { get; private set; } = 0;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                ErrorCount++;
        }
    }
}

[TestFixture]
public sealed class ThreadSafetySmokeTests() : ProviderTestBase("{\"Arr\":[1,2,3]}")
{
    [Test]
    public async Task RapidMixedWrites_NoCorruption()
    {
        var p = CreateProvider();
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            if (i % 3 == 0)
                p.Set("Arr", new[] { i });
            else
                p.Set("Val", i.ToString());
        })).ToArray();
        await Task.WhenAll(tasks);
        await p.FlushAsync();
        Assert.Multiple(() =>
        {
            Assert.That(p.TryGet("Val", out _), Is.True);
            Assert.That(p.TryGet("Arr:0", out var first), Is.True);
            Assert.That(first, Is.Not.Null);
        });
    }
}