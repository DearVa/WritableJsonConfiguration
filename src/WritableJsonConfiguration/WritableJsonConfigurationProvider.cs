#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace WritableJsonConfiguration;

public class WritableJsonConfigurationProvider : JsonConfigurationProvider
{
    private readonly string _fileFullPath;

    public WritableJsonConfigurationProvider(JsonConfigurationSource source) : base(source)
    {
        _fileFullPath = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty).PhysicalPath ??
            throw new FileNotFoundException("Json configuration file not found");
    }

    public override void Load(Stream stream)
    {
        Data = new JsonParser().ParseStream(stream);
    }

    private void Save(JsonNode jsonObj)
    {
        var output = jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_fileFullPath, output);
    }

    private void SetValue(string key, string? value, JsonNode jsonObj)
    {
        base.Set(key, value);
        var split = key.Split(':');
        var context = jsonObj;

        for (var i = 0; i < split.Length; i++)
        {
            var currentKey = split[i];
            if (i < split.Length - 1)
            {
                if (int.TryParse(currentKey, out var index) && context is JsonArray array)
                {
                    while (array.Count <= index)
                    {
                        array.Add(null);
                    }

                    context = array[index] ??= CreateChild();
                }
                else
                {
                    context = context[currentKey] ??= CreateChild();
                }

                JsonNode CreateChild() =>
                    i + 1 < split.Length && int.TryParse(split[i + 1], out _) ? new JsonArray() : new JsonObject();
            }
            else
            {
                switch (context)
                {
                    case JsonArray array when int.TryParse(currentKey, out var index):
                    {
                        while (array.Count <= index)
                        {
                            array.Add(null);
                        }

                        array[index] = JsonValue.Create(value);
                        break;
                    }
                    case JsonObject obj:
                    {
                        obj[currentKey] = JsonValue.Create(value);
                        break;
                    }
                }
            }
        }
    }

    private JsonNode GetJsonObj()
    {
        var json = File.Exists(_fileFullPath) ? File.ReadAllText(_fileFullPath) : "{}";
        return JsonNode.Parse(json) ?? new JsonObject();
    }

    public override void Set(string key, string? value)
    {
        var jsonObj = GetJsonObj();
        SetValue(key, value, jsonObj);
        Save(jsonObj);
    }

    public void Set(string key, object value)
    {
        var jsonNode = JsonSerializer.SerializeToNode(value);
        var jsonObj = GetJsonObj();
        WalkAndSet(key, jsonNode, jsonObj);
        Save(jsonObj);
    }

    private void WalkAndSet(string key, JsonNode? value, JsonNode jsonObj)
    {
        switch (value)
        {
            case JsonArray jArray:
            {
                for (var index = 0; index < jArray.Count; index++)
                {
                    var currentKey = $"{key}:{index}";
                    var elementValue = jArray[index];
                    WalkAndSet(currentKey, elementValue, jsonObj);
                }
                break;
            }
            case JsonObject jObject:
            {
                foreach (var property in jObject.AsObject())
                {
                    var propName = property.Key;
                    var currentKey = key == null ? propName : $"{key}:{propName}";
                    WalkAndSet(currentKey, property.Value, jsonObj);
                }
                break;
            }
            case JsonValue jValue:
            {
                SetValue(key, jValue.ToString(), jsonObj);
                break;
            }
            default:
            {
                SetValue(key, null, jsonObj);
                break;
            }
        }
    }

    /// <summary>
    /// modify from Microsoft.Extensions.Configuration.Json.JsonConfigurationFileParser, fixed null issues
    /// </summary>
    private class JsonParser
    {
        private readonly Dictionary<string, string?> _data = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<string> _paths = new();

        public Dictionary<string, string?> ParseStream(Stream input)
        {
            var jsonDocumentOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            using (var reader = new StreamReader(input))
            using (var doc = JsonDocument.Parse(reader.ReadToEnd(), jsonDocumentOptions))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
                VisitObjectElement(doc.RootElement);
            }

            return _data;
        }

        private void VisitObjectElement(JsonElement element)
        {
            var isEmpty = true;

            foreach (var property in element.EnumerateObject())
            {
                isEmpty = false;
                EnterContext(property.Name);
                VisitValue(property.Value);
                ExitContext();
            }

            SetNullIfElementIsEmpty(isEmpty);
        }

        private void VisitArrayElement(JsonElement element)
        {
            var index = 0;

            foreach (var arrayElement in element.EnumerateArray())
            {
                EnterContext(index.ToString());
                VisitValue(arrayElement);
                ExitContext();
                index++;
            }

            SetNullIfElementIsEmpty(isEmpty: index == 0);
        }

        private void SetNullIfElementIsEmpty(bool isEmpty)
        {
            if (isEmpty && _paths.Count > 0)
            {
                _data[_paths.Peek()] = null;
            }
        }

        private void VisitValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    VisitObjectElement(value);
                    break;
                }
                case JsonValueKind.Array:
                {
                    VisitArrayElement(value);
                    break;
                }
                case JsonValueKind.Number:
                case JsonValueKind.String:
                case JsonValueKind.True:
                case JsonValueKind.False:
                {
                    var key = _paths.Peek();
                    _data[key] = value.ToString();
                    break;
                }
                case JsonValueKind.Null:
                {
                    var key = _paths.Peek();
                    _data[key] = null;
                    break;
                }
            }
        }

        private void EnterContext(string context) =>
            _paths.Push(_paths.Count > 0 ?
                _paths.Peek() + ConfigurationPath.KeyDelimiter + context :
                context);

        private void ExitContext() => _paths.Pop();
    }
}