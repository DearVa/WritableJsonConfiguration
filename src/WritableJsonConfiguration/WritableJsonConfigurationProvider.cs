using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace WritableJsonConfiguration;

/// <summary>
/// A sophisticated, thread-safe, and high-performance writable JSON configuration provider.
/// It employs ReaderWriterLockSlim for optimized concurrent access in read-heavy scenarios,
/// and a debounced, asynchronous mechanism for file writing to minimize I/O overhead.
/// </summary>
public sealed class WritableJsonConfigurationProvider : JsonConfigurationProvider, IDisposable
{
    private const int DebounceMilliseconds = 200;

    private readonly string _fileFullPath;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly CancellationTokenSource _cts = new();
    private volatile Task _saveTask = Task.CompletedTask;
    private JsonNode? _jsonObj;

    public WritableJsonConfigurationProvider(JsonConfigurationSource source) : base(source)
    {
        _fileFullPath = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty).PhysicalPath
            ?? throw new FileNotFoundException("JSON configuration file not found.");
    }

    public override void Load(Stream stream)
    {
        // The base.Load(stream) will populate the `Data` dictionary.
        // We must ensure our internal JsonNode representation is also loaded and consistent.
        _lock.EnterWriteLock();
        try
        {
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            _jsonObj = JsonNode.Parse(json) ?? new JsonObject();
            Data = PopulateDataFromNode(_jsonObj);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static Dictionary<string, string?> PopulateDataFromNode(JsonNode root)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string? Path, JsonNode Node)>();
        stack.Push((null, root));

        while (stack.Count > 0)
        {
            var (path, node) = stack.Pop();

            switch (node)
            {
                case JsonObject obj:
                {
                    if (obj.Count == 0)
                    {
                        SetNullIfPathExists(path, data);
                    }
                    else
                    {
                        foreach (var property in obj)
                        {
                            var currentPath = string.IsNullOrEmpty(path) ? property.Key : ConfigurationPath.Combine(path, property.Key);
                            if (property.Value != null)
                            {
                                stack.Push((currentPath, property.Value));
                            }
                            else
                            {
                                data[currentPath] = null;
                            }
                        }
                    }
                    break;
                }
                case JsonArray array:
                {
                    if (array.Count == 0)
                    {
                        SetNullIfPathExists(path, data);
                    }
                    else
                    {
                        for (var i = 0; i < array.Count; i++)
                        {
                            var currentPath =
                                string.IsNullOrEmpty(path) ?
                                    i.ToString() :
                                    ConfigurationPath.Combine(path, i.ToString());
                            if (array[i] != null)
                            {
                                stack.Push((currentPath, array[i]!));
                            }
                            else
                            {
                                data[currentPath] = null;
                            }
                        }
                    }
                    break;
                }
                case JsonValue value:
                {
                    if (path != null)
                    {
                        data[path] = value.ToString();
                    }
                    break;
                }
            }
        }
        return data;
    }

    private static void SetNullIfPathExists(string? path, Dictionary<string, string?> data)
    {
        if (path != null)
        {
            data[path] = null;
        }
    }

    public override bool TryGet(string key, out string? value)
    {
        _lock.EnterReadLock();
        try
        {
            return base.TryGet(key, out value);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public override void Set(string key, string? value)
    {
        _lock.EnterWriteLock();
        try
        {
            // Update the base Data dictionary
            base.Set(key, value);

            // Update our in-memory JsonNode representation
            UpdateJsonNode(key, value);

            // Trigger a debounced save operation
            QueueSave();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Sets a complex object value in the configuration.
    /// The object is serialized to JSON and its properties are recursively set.
    /// </summary>
    /// <param name="key">The root key to set the object at. Can be null to set at the root level.</param>
    /// <param name="value">The object to set.</param>
    public void Set(string? key, object? value)
    {
        _lock.EnterWriteLock();
        try
        {
            var node = JsonSerializer.SerializeToNode(value);
            WalkAndSetNode(key, node);
            QueueSave();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void WalkAndSetNode(string? currentPath, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jObject:
            {
                foreach (var property in jObject)
                {
                    var nextPath = string.IsNullOrEmpty(currentPath) ? property.Key : ConfigurationPath.Combine(currentPath, property.Key);
                    WalkAndSetNode(nextPath, property.Value);
                }
                break;
            }
            case JsonArray jArray:
            {
                for (var i = 0; i < jArray.Count; i++)
                {
                    var nextPath = string.IsNullOrEmpty(currentPath) ? i.ToString() : ConfigurationPath.Combine(currentPath, i.ToString());
                    WalkAndSetNode(nextPath, jArray[i]);
                }
                break;
            }
            case JsonValue jValue when currentPath is not null:
            {
                var stringValue = jValue.TryGetValue<object>(out var v) ? v.ToString() : null;
                base.Set(currentPath, stringValue);
                UpdateJsonNode(currentPath, stringValue);
                break;
            }
            case null when currentPath is not null:
            {
                base.Set(currentPath, null);
                UpdateJsonNode(currentPath, null);
                break;
            }
        }
    }

    private void UpdateJsonNode(string key, string? value)
    {
        _jsonObj ??= new JsonObject();
        var context = _jsonObj;
        var segments = key.Split(ConfigurationPath.KeyDelimiter);

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (i == segments.Length - 1)
            {
                // Last segment, set the value
                SetNodeValue(context, segment, value);
                break;
            }

            // Navigate or create path
            context = GetOrCreateNextNode(context, segment, segments[i + 1]);
        }
    }

    private static JsonNode GetOrCreateNextNode(JsonNode context, string segment, string nextSegment)
    {
        if (int.TryParse(segment, out var index) && context is JsonArray array)
        {
            while (array.Count <= index) array.Add(null);
            return array[index] ??= int.TryParse(nextSegment, out _) ? new JsonArray() : new JsonObject();
        }

        if (context is JsonObject obj)
        {
            return obj[segment] ??= int.TryParse(nextSegment, out _) ? new JsonArray() : new JsonObject();
        }

        // This indicates a path mismatch, e.g., trying to access a property on an array by name.
        // For simplicity, we throw. A more robust implementation might handle this differently.
        throw new InvalidOperationException($"Cannot create child node on a non-object/array node at path segment '{segment}'.");
    }

    private static void SetNodeValue(JsonNode context, string segment, string? value)
    {
        var jsonValue = JsonValue.Create(value);
        if (int.TryParse(segment, out var index) && context is JsonArray array)
        {
            while (array.Count <= index) array.Add(null);
            array[index] = jsonValue;
        }
        else if (context is JsonObject obj)
        {
            obj[segment] = jsonValue;
        }
        else
        {
            throw new InvalidOperationException($"Cannot set value on a non-object/array node at path segment '{segment}'.");
        }
    }

    private void QueueSave()
    {
        // If a save is already queued or running, this new request is implicitly covered.
        // We only need to start a new one if the previous one is complete.
        if (_saveTask.IsCompleted)
        {
            _saveTask = SaveAsync(_cts.Token);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        // Debounce: wait for a short period before writing to disk.
        await Task.Delay(DebounceMilliseconds, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        string content;
        _lock.EnterReadLock(); // Only need a read lock to serialize the object
        try
        {
            content = _jsonObj?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // --- Atomic Write using Write-and-Rename Pattern ---

        // 1. Get a temporary file path in the same directory.
        // This is crucial for the atomic move operation to work reliably.
        var tempFilePath = Path.Combine(Path.GetDirectoryName(_fileFullPath)!, Path.GetRandomFileName());

        try
        {
            // 2. Write the new content to the temporary file.
            await File.WriteAllTextAsync(tempFilePath, content, Encoding.UTF8, cancellationToken);

            // 3. Atomically replace the original file with the new one.
            // On most filesystems, this is an atomic operation.
            File.Replace(tempFilePath, _fileFullPath, null);
        }
        catch
        {
            // If any error occurs, ensure the temporary file is cleaned up.
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                    // Ignore exceptions during cleanup. The original file is still intact.
                }
            }

            // Re-throw the original exception to signal that the save failed.
            throw;
        }
    }

    void IDisposable.Dispose()
    {
        _cts.Cancel();
        try
        {
            // Wait briefly for a pending save to complete or cancel.
            _saveTask.Wait(TimeSpan.FromSeconds(0.5));
        }
        catch (OperationCanceledException)
        {
            // Expected if the task was cancelled.
        }
        catch (Exception)
        {
            // Ignore other exceptions during disposal.
        }
        finally
        {
            _cts.Dispose();
            _lock.Dispose();
        }
    }
}