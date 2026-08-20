using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Bridges a typed record model to the existing
///     <see cref="SubrecordSchemaRegistry" />. Each encoder supplies a
///     <see cref="SubrecordSchema" />-keyed map of extractor delegates that pull values from
///     the model; this class invokes them, then delegates byte emission to
///     <see cref="SchemaDictionarySerializer" /> so there is exactly one place that knows how
///     to walk a schema and write PC little-endian bytes.
///     Throws if no schema is registered for the (signature, recordType, dataLength) triple —
///     fallback to hand-rolled bytes is an explicit decision the caller should not make
///     silently.
/// </summary>
internal static class SchemaModelSerializer
{
    /// <summary>
    ///     Serializes a typed model to PC little-endian subrecord bytes using the registered schema
    ///     and the supplied per-field value extractors; throws if no schema is registered.
    /// </summary>
    public static byte[] Serialize<TModel>(
        string signature,
        string recordType,
        int dataLength,
        TModel model,
        IReadOnlyDictionary<string, Func<TModel, object?>> fieldExtractors)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(signature, recordType, dataLength)
                     ?? throw new InvalidOperationException(
                         $"No schema registered for {recordType}/{signature} (dataLength={dataLength}).");

        var values = new Dictionary<string, object?>(fieldExtractors.Count, StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (field.Type == SubrecordFieldType.Padding)
            {
                continue;
            }

            if (fieldExtractors.TryGetValue(field.Name, out var extractor))
            {
                values[field.Name] = extractor(model);
            }
            // Missing extractors zero-fill via SchemaDictionarySerializer — matches its dict-path behavior.
        }

        return SchemaDictionarySerializer.Serialize(schema, values);
    }

    /// <summary>Serializes a typed model and wraps the bytes in an <see cref="EncodedSubrecord" /> with the given signature.</summary>
    public static EncodedSubrecord SerializeSubrecord<TModel>(
        string signature,
        string recordType,
        int dataLength,
        TModel model,
        IReadOnlyDictionary<string, Func<TModel, object?>> fieldExtractors)
    {
        return new EncodedSubrecord(
            signature,
            Serialize(signature, recordType, dataLength, model, fieldExtractors));
    }
}
