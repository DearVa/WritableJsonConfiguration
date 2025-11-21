using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace WritableJsonConfiguration;

/// <summary>
/// Simple write error event args (cross-platform; no severity classification).
/// </summary>
public sealed class ConfigurationWriteErrorEventArgs(
    Exception exception,
    int attempt,
    string filePath,
    int contentLength
) : EventArgs
{
    public Exception Exception { get; } = exception;
    public int Attempt { get; } = attempt;
    public string FilePath { get; } = filePath;
    public int ContentLength { get; } = contentLength;
}

/// <summary>
/// A sophisticated, thread-safe, and high-performance writable JSON configuration provider.
/// It employs ReaderWriterLockSlim for optimized concurrent access in read-heavy scenarios,
/// and a debounced, asynchronous mechanism for file writing to minimize I/O overhead.
/// </summary>
public sealed class WritableJsonConfigurationProvider : JsonConfigurationProvider, IDisposable
{
    /// <summary>
    /// Event triggered when a write error occurs during file save operations.
    /// Subscribers can use this event to log errors or implement retry logic.
    /// </summary>
    public event EventHandler<ConfigurationWriteErrorEventArgs>? WriteError;

    private const int DebounceMilliseconds = 200;

    private readonly string _fileFullPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly CancellationTokenSource _cts = new();

    private JsonNode? _jsonObj;

    // Initialize to a completed task to simplify the logic.
    private Task _saveTask = Task.CompletedTask;
    private int _saveQueued; // 0 = false, 1 = true

    public WritableJsonConfigurationProvider(JsonConfigurationSource source) : base(source)
    {
        _fileFullPath = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty).PhysicalPath
            ?? throw new FileNotFoundException("JSON configuration file not found.");
        _jsonOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IgnoreReadOnlyProperties = true
        };
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
        // Serialize outside the lock to prevent potential deadlocks if serialization
        // logic tries to read configuration.
        var node = JsonSerializer.SerializeToNode(value, _jsonOptions);

        _lock.EnterWriteLock();
        try
        {
            ReplaceSubtree(key, node);
            QueueSave();
        }
        finally
        {
            _lock.ExitWriteLock();
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

    public void Flush() => FlushAsync().GetAwaiter().GetResult(); // Force synchronous flush

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _saveQueued, 0);
        await SaveSnapshotToFileAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    private void QueueSave()
    {
        // Mark that a save is desired.
        Interlocked.Exchange(ref _saveQueued, 1);

        // If the save task is already completed, start a new one.
        // This check is not atomic, but SaveLoopAsync handles the race condition internally.
        if (_saveTask.IsCompleted)
        {
            _saveTask = SaveLoopAsync();
        }
    }

    private async Task SaveLoopAsync()
    {
        // Loop while there are pending saves or cancellation hasn't been requested yet.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Debounce: wait for a quiet period.
                await Task.Delay(DebounceMilliseconds, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // This is expected when Dispose is called. Break the loop to perform a final save.
                break;
            }

            // Atomically check if a save is needed and reset the flag.
            if (Interlocked.Exchange(ref _saveQueued, 0) == 0)
            {
                // No save was requested during the delay. We can exit the loop.
                break;
            }

            // Perform the save.
            await SaveSnapshotToFileAsync(false, _cts.Token);

            if (Volatile.Read(ref _saveQueued) == 0) break;
        }

        // After the loop (either by breaking or cancellation),
        // there might be a pending save request that came in after the last check.
        // Perform one final save if needed, ensuring data is not lost on disposal.
        if (Interlocked.Exchange(ref _saveQueued, 0) == 1)
        {
            // Use CancellationToken.None to ensure this final write completes even if disposal has been initiated.
            await SaveSnapshotToFileAsync(true, CancellationToken.None);
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
            content = _jsonObj?.ToJsonString(_jsonOptions) ?? "{}";
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var bytesLen = Encoding.UTF8.GetByteCount(content);
        var dir = Path.GetDirectoryName(_fileFullPath)!;
        Directory.CreateDirectory(dir);

        // Create temp file in same directory (required for atomic replace/move semantics)
        var tempPath = Path.Combine(dir, Path.GetRandomFileName());

        // We allow at most 2 attempts (initial + 1 quick retry) to soften transient sharing issues.
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
                                 FileOptions.Asynchronous)) // Keep simple; avoid platform-specific flags
                {
                    var buffer = Encoding.UTF8.GetBytes(content);
                    await fs.WriteAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Overwrite strategy (cross-platform):
#if NET8_0_OR_GREATER
                // File.Move(..., overwrite: true) handles overwrite atomically per platform guarantees.
                File.Move(tempPath, _fileFullPath, overwrite: true);
#else
                if (File.Exists(_fileFullPath))
                {
                    try
                    {
                        // Prefer Replace when available (Windows atomic), works on Unix too.
                        File.Replace(tempPath, _fileFullPath, null);
                    }
                    catch
                    {
                        // Fallback: delete then move (small non-atomic window but acceptable here).
                        TryDeleteFileSafe(_fileFullPath);
                        File.Move(tempPath, _fileFullPath);
                    }
                }
                else
                {
                    File.Move(tempPath, _fileFullPath);
                }
#endif
                return; // Success
            }
            catch (OperationCanceledException)
            {
                // Respect cancellation
                break;
            }
            catch (Exception ex)
            {
                RaiseWriteError(ex, attempt, bytesLen, _fileFullPath);

                // Clean temp if still present
                SafeDelete(tempPath);

                if (attempt == MaxAttempts) break;

                // Quick minimal retry delay
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);

                // Recreate temp path for next attempt
                tempPath = Path.Combine(dir, Path.GetRandomFileName());
            }
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore
        }
    }

    private void RaiseWriteError(Exception ex, int attempt, int contentLength, string filePath)
    {
        try
        {
            WriteError?.Invoke(this, new ConfigurationWriteErrorEventArgs(ex, attempt, filePath, contentLength));
        }
        catch
        {
            // Ignore
        }
    }

    void IDisposable.Dispose()
    {
        _cts.Cancel();
        try
        {
            _saveTask.Wait();
        }
        catch
        {
            // Ignore
        }

        try
        {
            Flush();
        }
        catch
        {
            // Ignore
        }

        _cts.Dispose();
        _lock.Dispose();
    }
}