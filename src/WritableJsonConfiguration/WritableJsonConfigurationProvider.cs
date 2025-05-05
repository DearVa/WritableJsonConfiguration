using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;

namespace WritableJsonConfiguration
{
    public class WritableJsonConfigurationProvider : JsonConfigurationProvider
    {
        public WritableJsonConfigurationProvider(JsonConfigurationSource source) : base(source)
        {
        }

        private void Save(JsonNode jsonObj)
        {
            var fileFullPath = base.Source.FileProvider.GetFileInfo(base.Source.Path).PhysicalPath;
            var options = new JsonSerializerOptions { WriteIndented = true };
            string output = jsonObj.ToJsonString(options);
            File.WriteAllText(fileFullPath, output);
        }

        private void SetValue(string key, string value, JsonNode jsonObj)
        {
            base.Set(key, value);
            var split = key.Split(':');
            var context = jsonObj;
            
            for (int i = 0; i < split.Length; i++)
            {
                var currentKey = split[i];
                if (i < split.Length - 1)
                {
                    if (context is JsonObject contextObj)
                    {
                        if (!contextObj.ContainsKey(currentKey))
                        {
                            JsonNode newNode = i + 1 < split.Length && int.TryParse(split[i + 1], out _)
                                ? new JsonArray()
                                : new JsonObject();
                            contextObj[currentKey] = newNode;
                        }
                        context = contextObj[currentKey];
                    }
                }
                else
                {
                    if (int.TryParse(currentKey, out var index))
                    {
                        if (context is JsonArray array)
                        {
                            while (array.Count <= index)
                            {
                                array.Add(JsonValue.Create("")); // 填充数组到指定索引
                            }
                            array[index] = JsonValue.Create(value);
                        }
                    }
                    else if (context is JsonObject contextObj)
                    {
                        contextObj[currentKey] = JsonValue.Create(value);
                    }
                }
            }
        }

        private JsonNode GetJsonObj()
        {
            var fileFullPath = base.Source.FileProvider.GetFileInfo(base.Source.Path).PhysicalPath;
            var json = File.Exists(fileFullPath) ? File.ReadAllText(fileFullPath) : "{}";
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
            var jsonObj = GetJsonObj();
            var serialized = JsonSerializer.Serialize(value);
            var jsonNode = JsonNode.Parse(serialized);
            WalkAndSet(key, jsonNode, jsonObj);
            Save(jsonObj);
        }

        private void WalkAndSet(string key, JsonNode value, JsonNode jsonObj)
        {
            switch (value)
            {
                case JsonArray jArray:
                    for (int index = 0; index < jArray.Count; index++)
                    {
                        var currentKey = $"{key}:{index}";
                        var elementValue = jArray[index];
                        WalkAndSet(currentKey, elementValue, jsonObj);
                    }
                    break;

                case JsonObject jObject:
                    foreach (var property in jObject.AsObject())
                    {
                        var propName = property.Key;
                        var currentKey = key == null ? propName : $"{key}:{propName}";
                        WalkAndSet(currentKey, property.Value, jsonObj);
                    }
                    break;

                case JsonValue jValue:
                    SetValue(key, jValue.ToString(), jsonObj);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }
}