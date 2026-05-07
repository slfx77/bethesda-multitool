using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Rebases parsed FormIDs in semantic ESM model objects using an explicit FormID property registry.
/// </summary>
internal static class RecordCollectionFormIdRebaser
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> WritablePropertyCache = new();

    internal static RecordCollection Rebase(RecordCollection records, Func<uint, uint> mapFormId)
    {
        return (RecordCollection)CloneValue(records, nameof(RecordCollection), mapFormId)!;
    }

    private static object? CloneValue(object? value, string propertyName, Func<uint, uint> mapFormId)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        if (type == typeof(string) || type.IsEnum || type == typeof(decimal))
        {
            return value;
        }

        if (type == typeof(uint))
        {
            return EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName)
                ? mapFormId((uint)value)
                : value;
        }

        if (type == typeof(uint?))
        {
            var nullable = (uint?)value;
            return nullable.HasValue && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName)
                ? mapFormId(nullable.Value)
                : nullable;
        }

        if (type.IsPrimitive)
        {
            return value;
        }

        if (type.IsArray)
        {
            return value;
        }

        if (value is IDictionary dictionary)
        {
            return CloneDictionary(dictionary, propertyName, mapFormId);
        }

        if (value is IList list)
        {
            return CloneList(list, propertyName, mapFormId);
        }

        if (!IsEsmModelType(type))
        {
            return value;
        }

        object clone;
        try
        {
            clone = Activator.CreateInstance(type)!;
        }
        catch (MissingMethodException)
        {
            return value;
        }

        foreach (var property in GetWritableProperties(type))
        {
            if (type == typeof(RecordCollection) && property.Name == nameof(RecordCollection.DialogueTree))
            {
                property.SetValue(clone, null);
                continue;
            }

            var originalPropertyValue = property.GetValue(value);
            var clonedPropertyValue = CloneValue(originalPropertyValue, property.Name, mapFormId);
            property.SetValue(clone, clonedPropertyValue);
        }

        return clone;
    }

    private static object CloneList(IList source, string propertyName, Func<uint, uint> mapFormId)
    {
        var listType = source.GetType();
        var elementType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
        var targetType = typeof(List<>).MakeGenericType(elementType);
        var target = (IList)Activator.CreateInstance(targetType)!;

        foreach (var item in source)
        {
            if (elementType == typeof(uint) && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName))
            {
                target.Add(mapFormId((uint)item!));
            }
            else
            {
                target.Add(CloneValue(item, propertyName, mapFormId));
            }
        }

        return target;
    }

    private static object CloneDictionary(IDictionary source, string propertyName, Func<uint, uint> mapFormId)
    {
        var dictionaryType = source.GetType();
        if (!dictionaryType.IsGenericType)
        {
            return source;
        }

        var genericArgs = dictionaryType.GetGenericArguments();
        var keyType = genericArgs[0];
        var valueType = genericArgs[1];
        var targetType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var target = (IDictionary)Activator.CreateInstance(targetType)!;
        var rebaseKeys = keyType == typeof(uint) &&
                         EsmFormIdPropertyRegistry.IsFormIdKeyedDictionary(propertyName);

        foreach (DictionaryEntry entry in source)
        {
            var key = rebaseKeys ? mapFormId((uint)entry.Key) : entry.Key;
            var value = CloneValue(entry.Value, propertyName, mapFormId);
            target[key] = value;
        }

        return target;
    }

    private static bool IsEsmModelType(Type type)
    {
        return type.Namespace != null &&
               type.Namespace.StartsWith("FalloutXbox360Utils.Core.Formats.Esm.Models", StringComparison.Ordinal);
    }

    private static PropertyInfo[] GetWritableProperties(Type type)
    {
        return WritablePropertyCache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.SetMethod != null && property.SetMethod.IsPublic)
            .ToArray());
    }
}
