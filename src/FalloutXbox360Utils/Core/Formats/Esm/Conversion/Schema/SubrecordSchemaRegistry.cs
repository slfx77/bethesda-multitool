using System.Collections.Concurrent;
using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Registry for subrecord schemas used in ESM/ESP endian conversion.
///     Provides schema lookup for determining how to byte-swap subrecord data.
/// </summary>
public static partial class SubrecordSchemaRegistry
{
    private const string? AnyRecordType = null;

    private static readonly Dictionary<SchemaKey, SubrecordSchema> _schemas = BuildSchemaRegistry();

    private static readonly HashSet<(string Signature, string? RecordType)>
        _stringSubrecords = BuildStringSubrecords();

    private static readonly ConcurrentDictionary<(string RecordType, string Subrecord, int DataLength, string
            FallbackType), int>
        _fallbackUsage = new();

    /// <summary>
    ///     Whether fallback logging is enabled.
    /// </summary>
    public static bool EnableFallbackLogging { get; set; }

    /// <summary>
    ///     Gets whether any fallbacks were recorded.
    /// </summary>
    public static bool HasFallbackUsage => !_fallbackUsage.IsEmpty;

    /// <summary>
    ///     Gets the schema for a subrecord, or null if no explicit schema exists.
    ///     Lookup priority:
    ///     1. Exact match (signature + recordType + dataLength)
    ///     2. Signature + recordType (any length)
    ///     3. Signature + dataLength (any record)
    ///     4. Signature only (default for that signature)
    /// </summary>
    public static SubrecordSchema? GetSchema(string signature, string recordType, int dataLength)
    {
        // IMAD records have special handling - most subrecords are float arrays
        if (recordType == "IMAD")
        {
            var imadSchema = GetImadSchema(signature);
            if (imadSchema != null)
            {
                return imadSchema;
            }
        }

        // Try exact match
        if (_schemas.TryGetValue(new SchemaKey(signature, recordType, dataLength), out var schema))
        {
            return schema;
        }

        // Try signature + recordType (any length)
        if (_schemas.TryGetValue(new SchemaKey(signature, recordType), out schema))
        {
            return schema;
        }

        // Try signature + dataLength (any record type)
        if (_schemas.TryGetValue(new SchemaKey(signature, null, dataLength), out schema))
        {
            return schema;
        }

        // Try signature only
        if (_schemas.TryGetValue(new SchemaKey(signature), out schema))
        {
            return schema;
        }

        // DATA fallback: mirror switch behavior for small fixed-size blocks
        if (signature == "DATA")
        {
            if (dataLength <= 2)
            {
                RecordFallback(recordType, signature, dataLength, "DATA-ByteArray-Small");
                return SubrecordSchema.ByteArray;
            }

            if (dataLength <= 64 && dataLength % 4 == 0)
            {
                RecordFallback(recordType, signature, dataLength, "DATA-FloatArray");
                return SubrecordSchema.FloatArray;
            }

            // Larger or irregular DATA blocks default to no swap
            RecordFallback(recordType, signature, dataLength, "DATA-ByteArray-Large");
            return SubrecordSchema.ByteArray;
        }

        // WTHR uses keyed *IAD subrecords (e.g., \x00IAD, @IAD, AIAD) for float pairs
        // These are NOT fallbacks - they're explicitly handled as float arrays
        if (recordType == "WTHR" && signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' &&
            signature[3] == 'D')
        {
            return SubrecordSchema.FloatArray;
        }

        return null;
    }

    /// <summary>
    ///     Records a fallback usage for diagnostics.
    /// </summary>
    public static void RecordFallback(string recordType, string subrecord, int dataLength, string fallbackType)
    {
        if (!EnableFallbackLogging)
            return;

        var key = (recordType, subrecord, dataLength, fallbackType);
        _fallbackUsage.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>
    ///     Clears all recorded fallback usage.
    /// </summary>
    public static void ClearFallbackLog()
    {
        _fallbackUsage.Clear();
    }

    /// <summary>
    ///     Gets the recorded fallback usage, grouped by type.
    /// </summary>
    public static IEnumerable<(string FallbackType, string RecordType, string Subrecord, int DataLength, int Count)>
        GetFallbackUsage()
    {
        return _fallbackUsage
            .Select(kvp => (
                kvp.Key.FallbackType,
                kvp.Key.RecordType,
                kvp.Key.Subrecord,
                kvp.Key.DataLength,
                Count: kvp.Value))
            .OrderBy(x => x.FallbackType)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.RecordType)
            .ThenBy(x => x.Subrecord);
    }

    /// <summary>
    ///     Gets all unique 4-character signatures registered in the schema.
    ///     Used for generic subrecord detection in memory dumps.
    /// </summary>
    public static IReadOnlySet<string> GetAllSignatures()
    {
        var signatures = new HashSet<string>();

        // Get signatures from schema registry
        foreach (var key in _schemas.Keys)
        {
            signatures.Add(key.Signature);
        }

        // Get signatures from string subrecords
        foreach (var (signature, _) in _stringSubrecords)
        {
            signatures.Add(signature);
        }

        return signatures;
    }

    /// <summary>
    ///     Gets schema for IMAD (Image Space Adapter) subrecords.
    ///     IMAD records have mostly float array subrecords.
    /// </summary>
    private static SubrecordSchema? GetImadSchema(string signature)
    {
        // EDID is a string - handled by IsStringSubrecord
        if (signature == "EDID")
        {
            return SubrecordSchema.String;
        }

        // Known float array subrecords in IMAD
        if (signature is "DNAM" or "BNAM" or "VNAM" or "TNAM" or "NAM3" or "RNAM" or "SNAM"
            or "UNAM" or "NAM1" or "NAM2" or "WNAM" or "XNAM" or "YNAM" or "NAM4")
        {
            return SubrecordSchema.FloatArray;
        }

        // Keyed *IAD subrecords (e.g., @IAD, AIAD, BIAD, etc.) - time/value float pairs
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
        {
            return SubrecordSchema.FloatArray;
        }

        // Unknown IMAD subrecord - treat as float array if divisible by 4
        return SubrecordSchema.FloatArray;
    }

    /// <summary>
    ///     Gets the byte-reversed signature for big-endian detection.
    ///     E.g., "EDID" -> "DIDE"
    /// </summary>
    public static string GetReversedSignature(string signature)
    {
        var chars = signature.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>
    ///     Checks if a subrecord contains string data (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        // Check record-specific string signatures first (more specific)
        if (_stringSubrecords.Contains((signature, recordType)))
        {
            return true;
        }

        // Check global string signatures (universal like EDID, FULL, MODL)
        return _stringSubrecords.Contains((signature, null));
    }

    /// <summary>
    ///     Register a simple 4-byte schema.
    /// </summary>
    private static void RegisterSimple4Byte(Dictionary<SchemaKey, SubrecordSchema> schemas, string signature,
        string description)
    {
        schemas[new SchemaKey(signature)] = new SubrecordSchema(F.UInt32(description))
        {
            Description = description
        };
    }

    /// <summary>
    ///     Register a simple 4-byte FormID schema.
    /// </summary>
    private static void RegisterSimpleFormId(Dictionary<SchemaKey, SubrecordSchema> schemas, string signature,
        string description)
    {
        schemas[new SchemaKey(signature)] = new SubrecordSchema(F.FormId(description))
        {
            Description = description
        };
    }

    /// <summary>
    ///     Schema key for lookup - combines signature, optional record type, and optional data length.
    /// </summary>
    /// <param name="Signature">4-character subrecord signature.</param>
    /// <param name="RecordType">Parent record type (null for any).</param>
    /// <param name="DataLength">Data length constraint (null for any, or expected size).</param>
    public readonly record struct SchemaKey(string Signature, string? RecordType = null, int? DataLength = null);
}
