using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

/// <summary>
///     Generic runtime struct reader that uses PDB-derived field layouts to extract
///     field values from any FormType's C++ struct in a memory dump.
///     Produces GenericEsmRecord instances with populated Fields dictionaries.
/// </summary>
internal sealed class RuntimeGenericReader(
    RuntimeMemoryContext context,
    IReadOnlyDictionary<byte, int>? typeShifts = null)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly IReadOnlyDictionary<byte, int> _typeShifts = typeShifts ?? new Dictionary<byte, int>();

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

        // pAllForms is NiTMapBase<uint, TESForm*>, so the retained file offset / VA identifies
        // the TESForm subobject, not necessarily the complete-object base. Identity was read at
        // canonical TESForm-relative +4/+12 upstream. PDB field offsets, however, are relative
        // to the complete object: MSTT's TESForm begins at +20 and FLOR's at +12 in the verified
        // layout. Recover that base before applying any PDB field offset. TESForm-first classes
        // such as WRLD and ASPC have an interior offset of zero, making this a no-op for them.
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        // Resolve the complete object in VA space when the dump provides a mapping. The hash-table
        // entry points at the TESForm subobject, so apply the same interior-base correction to its
        // VA before mapping the real object base back to a file offset. Prefer the captured pointer;
        // TesFormOffset is only used to recover the VA when no pointer was retained on the entry.
        var tesFormVa = entry.TesFormPointer is { } pointer && pointer != 0
            ? pointer
            : _context.MinidumpInfo.FileOffsetToVirtualAddress(entry.TesFormOffset.Value);
        long? objectVa = tesFormVa.HasValue ? tesFormVa.Value - interiorOffset : null;
        var objectBase = entry.TesFormOffset.Value - interiorOffset;
        if (objectVa.HasValue)
        {
            var mappedObjectBase = _context.MinidumpInfo.VirtualAddressToFileOffset(objectVa.Value);
            if (!mappedObjectBase.HasValue)
            {
                return null;
            }

            // Keep a file offset as the downstream base: BSStringT diagnostics and the recovered
            // record's Offset are file-oriented even though the top-level read is VA-oriented.
            objectBase = mappedObjectBase.Value;
        }

        // Read the type-shift range before probing per-record correction. This makes the VA-range
        // check authoritative: a validator must never inspect flat bytes across a capture gap.
        var shift = _typeShifts.TryGetValue(formType, out var s) ? s : 0;
        var effectiveSize = GetEffectiveSize(layout, shift);
        var structData = objectVa.HasValue
            ? _context.ReadBytesAtVa(objectVa.Value, effectiveSize)
            : _context.ReadBytes(objectBase, effectiveSize);
        if (structData == null)
        {
            return null;
        }

        var correctedShift = TryCorrectShift(layout, shift, structData);
        var correctedSize = GetEffectiveSize(layout, correctedShift);
        if (correctedSize > structData.Length)
        {
            structData = objectVa.HasValue
                ? _context.ReadBytesAtVa(objectVa.Value, correctedSize)
                : _context.ReadBytes(objectBase, correctedSize);
            if (structData == null)
            {
                return null;
            }
        }

        shift = correctedShift;

        var fields = ReadFields(structData, readableFields, objectBase, shift);

        // Extract display name from TESFullName.cFullName (BSStringT) if present
        string? fullName = null;
        var fullNameField = layout.Fields.FirstOrDefault(f => f is { Name: "cFullName", Owner: "TESFullName" });
        if (fullNameField != null)
        {
            var nameOffset = ApplyFieldShift(fullNameField, shift);
            fullName = _context.ReadBSStringTDiag(structData, nameOffset, out var nameFailure,
                out var namePtr, out var nameLen, out var nameHex, out var namePartial);
            BSStringDiagnostics.RecordWithSample("cFullName", nameFailure,
                new BSStringDiagnostics.DiagSample(entry.FormId, entry.EditorId, entry.FormType,
                    objectBase, nameOffset, namePtr, nameLen, nameHex, namePartial));
        }

        // Extract model path from TESModel.cModel (BSStringT) if present
        string? modelPath = null;
        var modelField = layout.Fields.FirstOrDefault(f => f is { Name: "cModel", Owner: "TESModel" });
        if (modelField != null)
        {
            var modelOffset = ApplyFieldShift(modelField, shift);
            modelPath = _context.ReadBSStringTDiag(structData, modelOffset, out var modelFailure,
                out var modelPtr, out var modelLen, out var modelHex, out var modelPartial);
            BSStringDiagnostics.RecordWithSample("cModel", modelFailure,
                new BSStringDiagnostics.DiagSample(entry.FormId, entry.EditorId, entry.FormType,
                    objectBase, modelOffset, modelPtr, modelLen, modelHex, modelPartial));
        }

        // Extract bounds from TESBoundObject.BoundData (12 bytes = 6 × int16) if present
        ObjectBounds? bounds = null;
        var boundsField =
            layout.Fields.FirstOrDefault(f => f is { Name: "BoundData", Owner: "TESBoundObject", Size: 12 });
        if (boundsField != null)
        {
            var bOffset = ApplyFieldShift(boundsField, shift);
            if (bOffset + 12 <= structData.Length)
            {
                bounds = RecordParserContext.ReadObjectBounds(
                    structData.AsSpan(bOffset, 12), true);
                if (bounds is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 })
                {
                    bounds = null;
                }
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
            Offset = objectBase,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read all readable fields from a struct data buffer using PDB field layouts.
    /// </summary>
    private Dictionary<string, object?> ReadFields(
        byte[] structData, IReadOnlyList<PdbFieldLayout> fields, long tesFormFileOffset, int shift = 0)
    {
        var result = new Dictionary<string, object?>(fields.Count);

        foreach (var field in fields)
        {
            var effectiveOffset = ApplyFieldShift(field, shift);
            if (effectiveOffset + field.Size > structData.Length || effectiveOffset < 0)
            {
                continue;
            }

            var key = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;
            var value = ReadFieldValue(structData, field, tesFormFileOffset, effectiveOffset);
            if (value != null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    ///     Use BSStringT fields as validators to detect per-record shift misalignment.
    ///     If the type-level shift produces LengthTooLarge on a BSStringT field, try ±4
    ///     and return the corrected shift. This fixes ~5% of records where the uniform
    ///     per-type shift is wrong for an individual record.
    /// </summary>
    private int TryCorrectShift(PdbTypeLayout layout, int typeShift, byte[] structData)
    {
        // Find a BSStringT field to use as a validator (prefer cModel — higher success rate)
        var probeField = layout.Fields.FirstOrDefault(f => f is { Name: "cModel", Owner: "TESModel" })
                         ?? layout.Fields.FirstOrDefault(f => f is { Name: "cFullName", Owner: "TESFullName" });
        if (probeField == null)
        {
            return typeShift;
        }

        // Test the type-level shift first
        var baseOffset = ApplyFieldShift(probeField, typeShift);
        if (!ContainsBsStringHeader(structData, baseOffset))
        {
            return typeShift;
        }

        _context.ReadBSStringTDiag(structData, baseOffset, out var baseFailure);

        // Only attempt correction for shift-related failures
        if (baseFailure is not (RuntimeMemoryContext.BSStringFailure.LengthTooLarge
            or RuntimeMemoryContext.BSStringFailure.InvalidPointer
            or RuntimeMemoryContext.BSStringFailure.InvalidAscii))
        {
            return typeShift;
        }

        // Try ±4 from type shift
        int[] corrections = [typeShift - 4, typeShift + 4];
        foreach (var candidateShift in corrections)
        {
            var candidateOffset = ApplyFieldShift(probeField, candidateShift);
            if (!ContainsBsStringHeader(structData, candidateOffset))
            {
                continue;
            }

            var result = _context.ReadBSStringTDiag(structData, candidateOffset, out var failure);
            if (result != null && failure == RuntimeMemoryContext.BSStringFailure.None)
            {
                return candidateShift;
            }
        }

        return typeShift;
    }

    private static int GetEffectiveSize(PdbTypeLayout layout, int shift)
    {
        return layout.StructSize + Math.Max(shift, 0) + 8; // +8 headroom for shift correction
    }

    private static bool ContainsBsStringHeader(byte[] structData, int offset)
    {
        return offset >= 0 && offset <= structData.Length - 8;
    }

    /// <summary>
    ///     Apply shift to a field offset. TESForm-owned fields are never shifted (they're anchored).
    /// </summary>
    private static int ApplyFieldShift(PdbFieldLayout field, int shift)
    {
        return field.Owner is "TESForm" ? field.Offset : field.Offset + shift;
    }

    /// <summary>
    ///     Probe all FormTypes in the entry list to find per-type uniform shifts.
    ///     Returns a dictionary of FormType → shift for types where a non-zero shift scores better.
    /// </summary>
    public static IReadOnlyDictionary<byte, int>? ProbeAllTypeShifts(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> allEntries)
    {
        // Group entries by FormType, excluding types with specialized readers
        var byType = allEntries
            .Where(e => e.TesFormOffset.HasValue && !PdbStructLayouts.HasSpecializedReader(e.FormType))
            .GroupBy(e => e.FormType)
            .Where(g => g.Count() >= 3) // Need at least 3 samples for a meaningful probe
            .ToList();

        if (byType.Count == 0)
        {
            return null;
        }

        var shifts = new Dictionary<byte, int>();
        int[] shiftOptions = [-8, -4, 0, 4, 8];

        foreach (var group in byType)
        {
            var formType = group.Key;
            var layout = PdbStructLayouts.Get(formType);
            if (layout == null)
            {
                continue;
            }

            var readableFields = PdbStructLayouts.GetReadableFields(formType);
            if (readableFields.Count == 0)
            {
                continue;
            }

            // Build probe field specs from PDB fields
            var fieldSpecs = new List<RuntimeReaderFieldProbe.FieldSpec>();
            foreach (var field in readableFields)
            {
                if (field.Owner is "TESForm")
                {
                    continue; // Anchored, don't probe
                }

                var check = GetFieldProbeCheck(field);

                if (check == null)
                {
                    continue; // Only probe fields with strong validation signals
                }

                fieldSpecs.Add(new RuntimeReaderFieldProbe.FieldSpec(
                    field.Name, field.Offset, 1, check.Value));
            }

            if (fieldSpecs.Count < 2)
            {
                continue; // Need at least 2 probeable fields
            }

            var samples = group.Take(10).ToList();
            var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
            var result = RuntimeReaderFieldProbe.Probe(
                context, samples, fieldSpecs, 1, shiftOptions,
                layout.StructSize, $"Generic_{RuntimeBuildOffsets.GetRecordTypeCode(formType) ?? $"0x{formType:X2}"}",
                tesFormInteriorOffset: interiorOffset);

            if (result is { Margin: >= 2 } && result.Winner.Layout[1] != 0)
            {
                shifts[formType] = result.Winner.Layout[1];
            }
        }

        return shifts.Count > 0 ? shifts : null;
    }

    internal static RuntimeReaderFieldProbe.FieldCheck? GetFieldProbeCheck(PdbFieldLayout field)
    {
        return field.Kind switch
        {
            "pointer" => RuntimeReaderFieldProbe.FieldCheck.PointerToForm,
            "float32" or "float" => RuntimeReaderFieldProbe.FieldCheck.NormalFloat,
            "struct" when field.TypeDetail is "BSStringT<char>" =>
                RuntimeReaderFieldProbe.FieldCheck.BSStringT,
            _ => null
        };
    }

    /// <summary>
    ///     Read a single field value from the struct data based on its PDB type kind.
    /// </summary>
    internal object? ReadFieldValue(byte[] data, PdbFieldLayout field, long tesFormFileOffset,
        int effectiveOffset = -1)
    {
        var offset = effectiveOffset >= 0 ? effectiveOffset : field.Offset;

        return field.Kind switch
        {
            "uint32" or "enum" => BinaryUtils.ReadUInt32BE(data, offset),
            "int32" => BinaryUtils.ReadInt32BE(data, offset),
            "uint16" => BinaryUtils.ReadUInt16BE(data, offset),
            "int16" => BinaryUtils.ReadInt16BE(data, offset),
            "uint8" => data[offset],
            "int8" => (sbyte)data[offset],
            "bool" => data[offset] != 0,
            "float32" or "float" => ReadValidatedFloat(data, offset),
            "pointer" => ReadPointerField(data, field, offset),
            "struct" => ReadEmbeddedStruct(data, field, offset),
            _ => null
        };
    }

    /// <summary>
    ///     Read a float field, returning null for non-finite or subnormal values (likely garbage data).
    /// </summary>
    private static float? ReadValidatedFloat(byte[] data, int offset)
    {
        var value = BinaryUtils.ReadFloatBE(data, offset);
        if (!RuntimeMemoryContext.IsNormalOrZeroFloat(value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read a pointer field. For pointers to TESForm-derived types, follow the pointer
    ///     and return the target FormID. For other pointers, return the raw VA.
    /// </summary>
    private uint? ReadPointerField(byte[] data, PdbFieldLayout field, int effectiveOffset)
    {
        var va = BinaryUtils.ReadUInt32BE(data, effectiveOffset);
        if (va == 0)
        {
            return null;
        }

        // Try to follow as a TESForm pointer and get the target's FormID
        var formId = _context.FollowPointerToFormId(data, effectiveOffset);
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
    ///     For larger ones, hand back the raw big-endian bytes.
    ///     <para>
    ///         The large-struct arm used to return a <c>"[TypeName, NB]"</c> descriptor that carried
    ///         no data, which made every embedded block over 8 bytes structurally unemittable —
    ///         CAMS's 40-byte CAMERA_SHOT_DATA among them. Returning the bytes lets an encoder run
    ///         them through the endian oracle
    ///         (<c>SubrecordSchemaProcessor.ConvertWithSchema</c>) instead of inventing a value.
    ///         <c>GenericRecordFields.TryBytes</c> already accepted a <c>byte[]</c>, so the change
    ///         is transparent to every existing caller.
    ///     </para>
    /// </summary>
    private object? ReadEmbeddedStruct(byte[] data, PdbFieldLayout field, int effectiveOffset)
    {
        if (field.Size <= 0 || effectiveOffset + field.Size > data.Length)
        {
            return null;
        }

        // TESBoundObject::BOUND_DATA — parse as readable bounds string
        if (field.TypeDetail is "TESBoundObject::BOUND_DATA" && field.Size == 12)
        {
            var b = RecordParserContext.ReadObjectBounds(
                data.AsSpan(effectiveOffset, 12), true);
            if (b is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 })
            {
                return null;
            }

            return b.ToString();
        }

        // BSStringT<char> is 8 bytes (4B pointer + 2B length + 2B maxLength) — try to resolve
        if (field.TypeDetail is "BSStringT<char>")
        {
            var str = _context.ReadBSStringTDiag(data, effectiveOffset, out _);
            if (str != null)
            {
                return str;
            }

            return null; // Null pointer or empty string — skip rather than showing hex
        }

        // For very small structs (up to 8 bytes), show as hex
        if (field.Size <= 8)
        {
            return Convert.ToHexString(data, effectiveOffset, field.Size);
        }

        // Larger structs: hand back the raw big-endian bytes so an encoder can convert them.
        return data.AsSpan(effectiveOffset, field.Size).ToArray();
    }
}
