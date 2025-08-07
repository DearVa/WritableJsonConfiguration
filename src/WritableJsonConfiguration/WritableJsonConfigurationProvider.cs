using System.Text.Json;
using System.Text.Json.Nodes;
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

    private void Save(JsonNode jsonObj)
    {
        var output = jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_fileFullPath, output);
    }

    private void SetValue(string key, string value, JsonNode jsonObj)
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
        return JsonNode.Parse(json);
    }

    public override void Set(string key, string value)
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

    private void WalkAndSet(string key, JsonNode value, JsonNode jsonObj)
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
}