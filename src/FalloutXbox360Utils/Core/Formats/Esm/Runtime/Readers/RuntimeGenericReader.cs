using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Generic runtime struct reader that uses PDB-derived field layouts to extract
///     field values from any FormType's C++ struct in a memory dump.
///     Produces GenericEsmRecord instances with populated Fields dictionaries.
/// </summary>
internal sealed class RuntimeGenericReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    /// <summary>
    ///     Read a runtime struct for the given entry and return a GenericEsmRecord
    ///     with all readable fields populated. Returns null if the entry has no
    ///     PDB layout or the struct data cannot be read.
    /// </summary>
    public GenericEsmRecord? ReadGenericRecord(RuntimeEditorIdEntry entry)
    {
        if (!entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var formType = entry.FormType;
        var layout = PdbStructLayouts.Get(formType);
        if (layout == null)
        {
            return null;
        }

        var readableFields = PdbStructLayouts.GetReadableFields(formType);
        if (readableFields.Count == 0)
        {
            return null;
        }

        var structData = _context.ReadBytes(entry.TesFormOffset.Value, layout.StructSize);
        if (structData == null)
        {
            return null;
        }

        var fields = ReadFields(structData, readableFields, entry.TesFormOffset.Value);

        // Extract display name from TESFullName.cFullName (BSStringT) if present
        string? fullName = null;
        var fullNameField = layout.Fields.FirstOrDefault(
            f => f is { Name: "cFullName", Owner: "TESFullName" });
        if (fullNameField != null)
        {
            fullName = _context.ReadBSStringT(entry.TesFormOffset.Value, fullNameField.Offset);
        }

        // Extract model path from TESModel.cModel (BSStringT) if present
        string? modelPath = null;
        var modelField = layout.Fields.FirstOrDefault(
            f => f is { Name: "cModel", Owner: "TESModel" });
        if (modelField != null)
        {
            modelPath = _context.ReadBSStringT(entry.TesFormOffset.Value, modelField.Offset);
        }

        // Extract bounds from TESBoundObject.BoundData (12 bytes = 6 × int16) if present
        ObjectBounds? bounds = null;
        var boundsField = layout.Fields.FirstOrDefault(
            f => f is { Name: "BoundData", Owner: "TESBoundObject", Size: 12 });
        if (boundsField != null && boundsField.Offset + 12 <= structData.Length)
        {
            bounds = RecordParserContext.ReadObjectBounds(
                structData.AsSpan(boundsField.Offset, 12), bigEndian: true);
            if (bounds is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 })
            {
                bounds = null;
            }
        }

        var recordCode = RuntimeBuildOffsets.GetRecordTypeCode(formType) ?? $"0x{formType:X2}";

        return new GenericEsmRecord
        {
            FormId = entry.FormId,
            RecordType = recordCode,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Fields = fields,
            Offset = entry.TesFormOffset.Value,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read all readable fields from a struct data buffer using PDB field layouts.
    /// </summary>
    private Dictionary<string, object?> ReadFields(
        byte[] structData, IReadOnlyList<PdbFieldLayout> fields, long tesFormFileOffset)
    {
        var result = new Dictionary<string, object?>(fields.Count);

        foreach (var field in fields)
        {
            if (field.Offset + field.Size > structData.Length)
            {
                continue;
            }

            var key = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;
            var value = ReadFieldValue(structData, field, tesFormFileOffset);
            if (value != null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    ///     Read a single field value from the struct data based on its PDB type kind.
    /// </summary>
    private object? ReadFieldValue(byte[] data, PdbFieldLayout field, long tesFormFileOffset)
    {
        var offset = field.Offset;

        return field.Kind switch
        {
            "uint32" or "enum" => BinaryUtils.ReadUInt32BE(data, offset),
            "int32" => BinaryUtils.ReadInt32BE(data, offset),
            "uint16" => BinaryUtils.ReadUInt16BE(data, offset),
            "int16" => BinaryUtils.ReadInt16BE(data, offset),
            "uint8" => data[offset],
            "int8" => (sbyte)data[offset],
            "bool" => data[offset] != 0,
            "float" => ReadValidatedFloat(data, offset),
            "pointer" => ReadPointerField(data, field),
            "struct" => ReadEmbeddedStruct(data, field, tesFormFileOffset),
            _ => null
        };
    }

    /// <summary>
    ///     Read a float field, returning null for NaN/Infinity values (likely garbage data).
    /// </summary>
    private static object? ReadValidatedFloat(byte[] data, int offset)
    {
        var value = BinaryUtils.ReadFloatBE(data, offset);
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read a pointer field. For pointers to TESForm-derived types, follow the pointer
    ///     and return the target FormID. For other pointers, return the raw VA.
    /// </summary>
    private object? ReadPointerField(byte[] data, PdbFieldLayout field)
    {
        var va = BinaryUtils.ReadUInt32BE(data, field.Offset);
        if (va == 0)
        {
            return null;
        }

        // Try to follow as a TESForm pointer and get the target's FormID
        var formId = _context.FollowPointerToFormId(data, field.Offset);
        if (formId.HasValue)
        {
            return formId.Value;
        }

        // For BSStringT pointers (cFullName, cModel, etc.) — skip, handled separately
        if (field.TypeDetail is "BSStringT<char>")
        {
            return null;
        }

        // Return raw VA for non-TESForm pointers (only if valid)
        return _context.IsValidPointer(va) ? va : null;
    }

    /// <summary>
    ///     For BSStringT structs (8 bytes: pointer + length), resolve to the actual string.
    ///     For small embedded structs, read as a formatted hex string.
    ///     For larger ones, just note the type name and size.
    /// </summary>
    private object? ReadEmbeddedStruct(byte[] data, PdbFieldLayout field, long tesFormFileOffset)
    {
        if (field.Size <= 0 || field.Offset + field.Size > data.Length)
        {
            return null;
        }

        // TESBoundObject::BOUND_DATA — parse as readable bounds string
        if (field.TypeDetail is "TESBoundObject::BOUND_DATA" && field.Size == 12)
        {
            var b = RecordParserContext.ReadObjectBounds(
                data.AsSpan(field.Offset, 12), bigEndian: true);
            if (b is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 })
            {
                return null;
            }

            return b.ToString();
        }

        // BSStringT<char> is 8 bytes (4B pointer + 2B length + 2B maxLength) — try to resolve
        if (field.TypeDetail is "BSStringT<char>")
        {
            var str = _context.ReadBSStringT(tesFormFileOffset, field.Offset);
            if (str != null)
            {
                return str;
            }

            return null; // Null pointer or empty string — skip rather than showing hex
        }

        // For very small structs (up to 8 bytes), show as hex
        if (field.Size <= 8)
        {
            return Convert.ToHexString(data, field.Offset, field.Size);
        }

        // For larger structs, just describe them
        return $"[{field.TypeDetail ?? "struct"}, {field.Size}B]";
    }
}
