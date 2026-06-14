using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WritableJsonConfiguration;

/// <summary>
/// Represents an optional value that can be serialized to JSON. This is useful for distinguishing between a property that is not present in the JSON (undefined) and a property that is present with a null value.
/// </summary>
/// <typeparam name="T"></typeparam>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
[TypeConverter(typeof(JsonOptionalTypeConverter))]
public readonly struct JsonOptional<T>
{
    /// <summary>
    /// Indicates whether the value is defined (present in the JSON) or not. If false, the Value property will throw an exception when accessed.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Gets the value if it is defined. If HasValue is false, this property will throw an InvalidOperationException.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public T? Value => HasValue ? field : throw new InvalidOperationException("Value is undefined.");

    private JsonOptional(T? value)
    {
        HasValue = true;
        Value = value;
    }

    /// <summary>
    /// Represents an undefined value, which means the property was not present in the JSON.
    /// Accessing the Value property on this instance will throw an exception.
    /// </summary>
    public static JsonOptional<T> Undefined => default;

    /// <summary>
    /// Creates a JsonOptional{T} instance from a given value.
    /// The resulting instance will have HasValue set to true, even if the value is null.
    /// This allows distinguishing between a property that is explicitly set to null and a property that is not present in the JSON at all.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static JsonOptional<T> From(T? value) => new(value);

    /// <summary>
    /// Implicitly converts a value of type T? to a JsonOptional{T} instance.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static implicit operator JsonOptional<T>(T? value) => From(value);
}

public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public static void JsonTypeInfoResolverModifier(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

        foreach (var propertyInfo in typeInfo.Properties)
        {
            var propertyType = propertyInfo.PropertyType;
            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(JsonOptional<>))
            {
                propertyInfo.ShouldSerialize = static (_, value) =>
                    value?.GetType().GetProperty(nameof(JsonOptional<>.HasValue))?.GetValue(value) is true;
            }
        }
    }

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(JsonOptional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class OptionalJsonConverter<T> : JsonConverter<JsonOptional<T>>
{
    public override JsonOptional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return JsonOptional<T>.From(value);
    }

    public override void Write(Utf8JsonWriter writer, JsonOptional<T> value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
            throw new JsonException("Undefined Optional<T> should have been skipped by ShouldSerialize.");

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}

public sealed class JsonOptionalTypeConverter : TypeConverter
{
    private static readonly ConcurrentDictionary<Type, Func<object?, object>> Factories = new();

    private readonly Type _valueType;
    private readonly bool _isNullableValueType;
    private readonly TypeConverter _valueConverter;
    private readonly Func<object?, object> _createOptional;

    public JsonOptionalTypeConverter(Type type)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(JsonOptional<>))
        {
            throw new ArgumentException($"Type must be {typeof(JsonOptional<>)}.", nameof(type));
        }

        _valueType = type.GetGenericArguments()[0];
        var underlyingType = Nullable.GetUnderlyingType(_valueType);
        _isNullableValueType = underlyingType is not null;

        _valueConverter = TypeDescriptor.GetConverter(underlyingType ?? _valueType);
        _createOptional = CreateFactory(_valueType);
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) ? _valueConverter.CanConvertFrom(context, sourceType) : base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text) return base.ConvertFrom(context, culture, value);

        object? converted;
        if (string.IsNullOrEmpty(text) && _isNullableValueType)
        {
            converted = null;
        }
        else
        {
            converted = _valueConverter.ConvertFrom(context, culture ?? CultureInfo.InvariantCulture, text);
        }

        if (converted is null && _valueType.IsValueType && Nullable.GetUnderlyingType(_valueType) is null)
        {
            throw new NotSupportedException($"Cannot convert null or empty value to Optional<{_valueType.Name}>.");
        }

        return _createOptional(converted);
    }

    private static Func<object?, object> CreateFactory(Type valueType)
    {
        return Factories.GetOrAdd(valueType, static type =>
        {
            var method = typeof(JsonOptionalTypeConverter)
                .GetMethod(nameof(CreateCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type);
            return (Func<object?, object>)Delegate.CreateDelegate(typeof(Func<object?, object>), method);
        });
    }

#pragma warning disable CA1859
    private static object CreateCore<T>(object? value)
#pragma warning restore CA1859
    {
        return JsonOptional<T>.From((T?)value);
    }
}