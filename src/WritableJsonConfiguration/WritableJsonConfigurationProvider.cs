using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WritableJsonConfiguration;

/// <summary>
/// A sophisticated, thread-safe, and high-performance writable JSON configuration provider.
/// It employs ReaderWriterLockSlim for optimized concurrent access in read-heavy scenarios,
/// and a debounced, asynchronous mechanism for file writing to minimize I/O overhead.
/// </summary>
public sealed class WritableJsonConfigurationProvider : JsonConfigurationProvider, IDisposable
{
    private const int DebounceMilliseconds = 200;

    private readonly ILogger _logger;
    private readonly string _fileFullPath;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly CancellationTokenSource _cts = new();

    private JsonNode? _jsonObj;

    private readonly Task _saveLoopTask;
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _isDirty;

    public WritableJsonConfigurationProvider(WritableJsonConfigurationSource source, ILogger? logger = null) : base(source)
    {
        _logger = logger ?? NullLogger.Instance;

        _fileFullPath = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty).PhysicalPath
            ?? throw new FileNotFoundException("JSON configuration file not found.");
        _jsonSerializerOptions = source.JsonSerializerOptions;

        // Start the background save loop task.
        _saveLoopTask = Task.Run(SaveLoopAsync);
    }

    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        _lock.EnterReadLock();
        try
        {
            return base.GetChildKeys(earlierKeys, parentPath);
        }
        finally
        {
            _lock.ExitReadLock();
        }
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
            _jsonObj = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) ?? new JsonObject();
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
            ReplaceSubtree(key, value);

            // Trigger a debounced save operation
            SignalChange();
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
        // Serialize outside the lock to prevent potential deadlocks if serialization
        // logic tries to read configuration.
        var node = JsonSerializer.SerializeToNode(value, _jsonSerializerOptions);

        _lock.EnterWriteLock();
        try
        {
            ReplaceSubtree(key, node);
            SignalChange();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Signals that the configuration has changed and triggers the save loop.
    /// </summary>
    private void SignalChange()
    {
        _isDirty = true;

        // Release a signal if the count is 0.
        // We don't need to accumulate signals, just know that "there is a change".
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch
            { /* ignored */
            }
        }
    }

    /// <summary>
    /// Replace an entire subtree at the specified configuration path (atomic logical replacement).
    /// Ensures arrays shrink properly by removing stale indices.
    /// </summary>
    private void ReplaceSubtree(string? key, JsonNode? newNode)
    {
        _jsonObj ??= new JsonObject();

        // Root replacement
        if (string.IsNullOrEmpty(key))
        {
            if (newNode is null)
            {
                _jsonObj = new JsonObject();
                Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                return;
            }
            if (newNode is not JsonObject rootObj)
                throw new InvalidOperationException("Root must be a JSON object.");
            _jsonObj = rootObj;
            Data = PopulateDataFromNode(_jsonObj);
            return;
        }

        // Navigate/create parent container
        var segments = key.Split(ConfigurationPath.KeyDelimiter);
        var current = _jsonObj;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            var nextIsIndex = int.TryParse(segments[i + 1], out _);

            if (int.TryParse(seg, out var arrIndex))
            {
                if (current is not JsonArray arr)
                    throw new InvalidOperationException($"Path segment '{seg}' expects an array.");

                while (arr.Count <= arrIndex) arr.Add(null);
                current = arr[arrIndex] ??= nextIsIndex ? new JsonArray() : new JsonObject();
            }
            else
            {
                if (current is not JsonObject obj)
                    throw new InvalidOperationException($"Path segment '{seg}' expects an object.");

                current = obj[seg] ??= nextIsIndex ? new JsonArray() : new JsonObject();
            }
        }

        var last = segments[^1];

        switch (current)
        {
            // Apply physical JsonNode replacement
            case JsonObject parentObj when !int.TryParse(last, out _):
            {
                parentObj[last] = newNode;
                break;
            }
            case JsonArray parentArr when int.TryParse(last, out var lastIdx):
            {
                while (parentArr.Count <= lastIdx) parentArr.Add(null);
                parentArr[lastIdx] = newNode;
                break;
            }
            default:
            {
                throw new InvalidOperationException("Incompatible container for the final segment.");
            }
        }

        // Remove old flattened keys beneath this path
        var prefix = key + ConfigurationPath.KeyDelimiter;
        var toRemove = Data.Keys
            .Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var del in toRemove) Data.Remove(del);

        // Re-flatten new subtree
        if (newNode is null)
        {
            Data[key] = null;
        }
        else
        {
            FlattenNode(newNode, key, Data);
        }
    }

    /// <summary>
    /// Flatten a subtree into configuration key/value pairs.
    /// </summary>
    private static void FlattenNode(JsonNode node, string basePath, IDictionary<string, string?> target)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.Count == 0)
                {
                    target[basePath] = null;
                    return;
                }
                foreach (var kv in obj)
                {
                    var childPath = ConfigurationPath.Combine(basePath, kv.Key);
                    if (kv.Value is null)
                        target[childPath] = null;
                    else
                        FlattenNode(kv.Value, childPath, target);
                }
                break;
            }
            case JsonArray arr:
            {
                if (arr.Count == 0)
                {
                    target[basePath] = null;
                    return;
                }
                for (var i = 0; i < arr.Count; i++)
                {
                    var childPath = ConfigurationPath.Combine(basePath, i.ToString());
                    var v = arr[i];
                    if (v is null)
                        target[childPath] = null;
                    else
                        FlattenNode(v, childPath, target);
                }
                break;
            }
            case JsonValue val:
            {
                target[basePath] = val.ToString();
                break;
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // Only force save if there are actual changes
        if (_isDirty)
        {
            await SaveSnapshotToFileAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // 1. Wait for signal
                await _signal.WaitAsync(_cts.Token);

                // 2. Debounce delay
                await Task.Delay(DebounceMilliseconds, _cts.Token);

                // 3. Consume all additional signals generated during the delay (avoid duplicate loops)
                while (_signal.CurrentCount > 0) await _signal.WaitAsync(0);

                // 4. If there are indeed changes, perform save
                if (_isDirty)
                {
                    await SaveSnapshotToFileAsync(false, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored
        }
        catch (Exception ex)
        {
            // Should not happen, prevent loop crash
            _logger.LogError(ex, "An exception occurred while writing to file.");
        }
    }

    private Task SaveSnapshotToFileAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            return DoAtomicWriteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            RaiseWriteError(ex, 0, -1, _fileFullPath);
            if (force) throw;
            return Task.CompletedTask;
        }
    }

    private async Task DoAtomicWriteAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        string content;
        _lock.EnterReadLock();
        try
        {
            content = _jsonObj?.ToJsonString(_jsonSerializerOptions) ?? "{}";

            // Key point: Reset the dirty flag while holding the lock.
            // This means "we have captured all changes up to this moment".
            // If new Set calls come in after releasing the lock, they will set _isDirty to true again and trigger the next save.
            _isDirty = false;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var bytesLen = Encoding.UTF8.GetByteCount(content);
        var dir = Path.GetDirectoryName(_fileFullPath)!;
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, Path.GetRandomFileName());
        const int MaxAttempts = 2;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using (var fs = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous))
                {
                    var buffer = Encoding.UTF8.GetBytes(content);
                    await fs.WriteAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

#if NET8_0_OR_GREATER
                File.Move(tempPath, _fileFullPath, overwrite: true);
#else
                if (File.Exists(_fileFullPath))
                {
                    try { File.Replace(tempPath, _fileFullPath, null); }
                    catch { TryDeleteFileSafe(_fileFullPath); File.Move(tempPath, _fileFullPath); }
                }
                else { File.Move(tempPath, _fileFullPath); }
#endif
                return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                RaiseWriteError(ex, attempt, bytesLen, _fileFullPath);
                SafeDelete(tempPath);
                if (attempt == MaxAttempts) break;
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                tempPath = Path.Combine(dir, Path.GetRandomFileName());
            }
        }
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary file '{TempPath}'", path);
        }
    }

    private void RaiseWriteError(Exception ex, int attempt, int contentLength, string filePath)
    {
        _logger.LogError(
            ex,
            "Failed to write JSON configuration to '{FilePath}' (Attempt {Attempt}, Content Length: {ContentLength} bytes)",
            filePath,
            attempt,
            contentLength);
    }

    void IDisposable.Dispose()
    {
        _cts.Cancel();
        try
        {
            _saveLoopTask.Wait();
        }
        catch
        {
            // Ignore
        }

        _cts.Dispose();
        _lock.Dispose();
        _signal.Dispose();
    }
}