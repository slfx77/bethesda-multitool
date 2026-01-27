using Xbox360MemoryCarver.Core.Converters.Esm.Schema;

namespace Xbox360MemoryCarver.Core.Converters.Esm;

/// <summary>
///     Converts subrecord data based on type and parent record.
///     Handles endian conversion for all known subrecord formats.
///     Uses schema-driven conversion.
/// </summary>
internal static partial class EsmSubrecordConverter
{
    /// <summary>
    ///     Converts subrecord data based on type.
    /// </summary>
    public static byte[] ConvertSubrecordData(string signature, ReadOnlySpan<byte> data, string recordType)
    {
        var schemaResult = SubrecordSchemaProcessor.ConvertWithSchema(signature, data, recordType);
        return schemaResult ?? throw new NotSupportedException(
            $"No schema for subrecord '{signature}' ({data.Length} bytes) in record type '{recordType}'.");
    }
}
