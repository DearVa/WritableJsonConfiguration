using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace WritableJsonConfiguration.Tests;

public abstract class ProviderTestBase : IDisposable
{
    protected readonly string TempDir;
    protected readonly string FilePath;

    protected ProviderTestBase(string? initialJson = null)
    {
        TempDir = Path.Combine(Path.GetTempPath(), "WritableCfgTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        FilePath = Path.Combine(TempDir, "appsettings.json");
        File.WriteAllText(FilePath, initialJson ?? "{}");
    }

    protected WritableJsonConfigurationProvider CreateProvider()
    {
        var source = new JsonConfigurationSource
        {
            Path = Path.GetFileName(FilePath),
            Optional = false,
            ReloadOnChange = false,
            FileProvider = new PhysicalFileProvider(TempDir)
        };
        source.ResolveFileProvider();
        var provider = new WritableJsonConfigurationProvider(source);
        using var fs = File.OpenRead(FilePath);
        provider.Load(fs);
        return provider;
    }

    protected string ReadFile() => File.ReadAllText(FilePath);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(TempDir, true);
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
    public void LeafSet_DoesNotRemoveSiblingKeys()
    {
        var p = CreateProvider();
        p.Set("Obj:A", "100");
        p.Flush();
        var obj = GetJson().GetProperty("Obj");
        Assert.Multiple(() =>
        {
            Assert.That(obj.GetProperty("A").GetString(), Is.EqualTo("100"));
            Assert.That(obj.GetProperty("B").GetString(), Is.EqualTo("2"));
            Assert.That(obj.GetProperty("C").GetString(), Is.EqualTo("3"));
        });
    }

    [Test]
    public void ObjectSubtreeReplacement_RemovesMissingKeys()
    {
        var p = CreateProvider();
        p.Set("Obj", new { A = "10", B = "20" });
        p.Flush();
        var obj = GetJson().GetProperty("Obj");
        Assert.Multiple(() =>
        {
            Assert.That(obj.TryGetProperty("A", out _), Is.True);
            Assert.That(obj.TryGetProperty("B", out _), Is.True);
            Assert.That(obj.TryGetProperty("C", out _), Is.False);
        });
    }

    [Test]
    public void ArrayShrink_RemovesStaleIndices()
    {
        var p = CreateProvider();
        p.Set("Numbers", new[] { 9, 8 });
        p.Flush();
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
    public void ArrayExpand_AddsNewIndices()
    {
        var p = CreateProvider();
        p.Set("Numbers", new[] { 5, 6, 7, 8, 9, 10 });
        p.Flush();
        var arr = GetJson().GetProperty("Numbers");
        Assert.Multiple(() =>
        {
            Assert.That(arr.GetArrayLength(), Is.EqualTo(6));
            Assert.That(arr[5].GetInt32(), Is.EqualTo(10));
        });
    }

    [Test]
    public void RootReplacement_ResetsContent()
    {
        var p = CreateProvider();
        p.Set(null, new { RootOnly = 1 });
        p.Flush();
        var j = GetJson();
        Assert.Multiple(() =>
        {
            Assert.That(j.TryGetProperty("RootOnly", out _), Is.True);
            Assert.That(j.TryGetProperty("Numbers", out _), Is.False);
        });
    }

    [Test]
    public void RootReplacement_WithNull_CreatesEmptyObject()
    {
        var p = CreateProvider();
        p.Set(null, null);
        p.Flush();
        var text = ReadFile().Trim().Replace("\r", "");
        Assert.That(text, Is.EqualTo("{}"));
    }

    [Test]
    public void ConcurrentWrites_ArrayIntegrityMaintained()
    {
        var p = CreateProvider();
        Parallel.For(0, 50, i =>
        {
            if (i % 2 == 0)
                p.Set("Numbers", Enumerable.Range(0, 3 + (i % 5)).ToArray());
            else
                p.Set("Obj:A", i.ToString());
        });
        p.Flush();
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
    public void Flush_ImmediatePersistence()
    {
        var p = CreateProvider();
        p.Set("Obj:A", "ZZ");
        p.Flush();
        var text = ReadFile();
        Assert.That(text, Does.Contain("\"A\": \"ZZ\""));
    }

    [Test]
    public void WriteError_EventRaised()
    {
        var p = CreateProvider();
        var attempts = new ConcurrentQueue<int>();
        p.WriteError += (_, e) => attempts.Enqueue(e.Attempt);

        // Force a write failure by making the target file read-only
        var originalAttributes = File.GetAttributes(FilePath);
        File.SetAttributes(FilePath, originalAttributes | FileAttributes.ReadOnly);

        try
        {
            p.Set("Obj:A", "LOCKED");

            // Flush may throw or not depending on implementation; we only care that event fired.
            try { p.Flush(); } catch { /* ignore */ }

            // If implementation debounces without Flush forcing, add a small wait (usually unnecessary).
            // Task.Delay(50).GetAwaiter().GetResult();

            Assert.That(attempts, Is.Not.Empty, "WriteError event was not raised.");
        }
        finally
        {
            // Restore attributes so temp directory cleanup succeeds
            File.SetAttributes(FilePath, originalAttributes);
        }
    }

    [Test]
    public void EmptyArrayAndObject_FlattenedAsNullEntries()
    {
        var p = CreateProvider();
        p.Set("Meta", new { EmptyArr = Array.Empty<int>(), EmptyObj = new { } });
        p.Flush();
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
