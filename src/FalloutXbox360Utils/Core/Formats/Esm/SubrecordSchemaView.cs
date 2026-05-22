using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Typed view over a single subrecord's schema-decoded field values. Pairs with
///     <see cref="Plugin.Writers.Encoders.SchemaModelSerializer" /> on the encoder side:
///     the encoder side throws when no schema is registered, and so does this — making
///     field-name discipline a hard contract across both halves of the pipeline.
///     <para>
///         Use <see cref="Read" /> for subrecords that MUST have a registered schema —
///         a missing schema indicates a bug (mismatched data length, typo'd signature,
///         or an unmigrated record type). Use <see cref="TryRead" /> for subrecords that
///         intentionally have no schema (e.g. AMMO/DAT2, which is heuristic).
///     </para>
/// </summary>
public sealed class SubrecordSchemaView
{
    private readonly Dictionary<string, object?> _fields;

    private SubrecordSchemaView(Dictionary<string, object?> fields)
    {
        _fields = fields;
    }

    /// <summary>
    ///     Reads the subrecord using the registered schema. Throws if no schema is
    ///     registered for the (signature, recordType, dataLength) tuple.
    /// </summary>
    public static SubrecordSchemaView Read(
        string signature,
        string? recordType,
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        var rt = recordType ?? string.Empty;
        var schema = SubrecordSchemaRegistry.GetSchema(signature, rt, data.Length);
        if (schema == null)
        {
            throw new InvalidOperationException(
                $"No schema registered for {rt}/{signature} (dataLength={data.Length}).");
        }

        return new SubrecordSchemaView(SubrecordSchemaReader.ReadFields(signature, data, rt, bigEndian));
    }

    /// <summary>
    ///     Soft-fail variant: returns null if no schema is registered. Use for
    ///     subrecords that are intentionally unschematized (heuristic parsing paths).
    /// </summary>
    public static SubrecordSchemaView? TryRead(
        string signature,
        string? recordType,
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        var rt = recordType ?? string.Empty;
        var schema = SubrecordSchemaRegistry.GetSchema(signature, rt, data.Length);
        if (schema == null)
        {
            return null;
        }

        return new SubrecordSchemaView(SubrecordSchemaReader.ReadFields(signature, data, rt, bigEndian));
    }

    /// <summary>
    ///     Underlying decoded field dictionary. Most callers should prefer the typed accessors
    ///     below — this exists for the small set of consumers (CSTY/LGTM/WATR/LSCT records)
    ///     that hold the dict verbatim instead of projecting to a typed model. Returned as a
    ///     mutable Dictionary to match the consumers' existing storage type; treat as read-only.
    /// </summary>
    public Dictionary<string, object?> Raw => _fields;

    public bool HasField(string name) => _fields.ContainsKey(name);

    public byte Byte(string name, byte def = 0) => SubrecordDataReader.GetByte(_fields, name, def);

    public sbyte SByte(string name, sbyte def = 0) => SubrecordDataReader.GetSByte(_fields, name, def);

    public ushort UInt16(string name, ushort def = 0) => SubrecordDataReader.GetUInt16(_fields, name, def);

    public short Int16(string name, short def = 0) => SubrecordDataReader.GetInt16(_fields, name, def);

    public uint UInt32(string name, uint def = 0) => SubrecordDataReader.GetUInt32(_fields, name, def);

    public int Int32(string name, int def = 0) => SubrecordDataReader.GetInt32(_fields, name, def);

    public float Float(string name, float def = 0f) => SubrecordDataReader.GetFloat(_fields, name, def);

    public string? String(string name) => SubrecordDataReader.GetString(_fields, name);

    public byte[]? Bytes(string name)
    {
        return _fields.TryGetValue(name, out var value) && value is byte[] arr ? arr : null;
    }

    /// <summary>
    ///     FormID accessor — returns null when the underlying value is zero, matching
    ///     the prevailing handler idiom (`if (formId != 0) record.SomeFormId = formId`).
    /// </summary>
    public uint? FormId(string name)
    {
        var value = SubrecordDataReader.GetUInt32(_fields, name, 0);
        return value != 0 ? value : null;
    }
}
