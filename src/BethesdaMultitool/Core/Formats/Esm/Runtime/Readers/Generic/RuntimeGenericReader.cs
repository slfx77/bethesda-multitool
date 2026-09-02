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

        if (ResolveStruct(entry, layout) is not var (structData, objectBase, shift))
        {
            return null;
        }

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
    ///     Read only the nested payloads a record carries behind a container or an indirection —
    ///     MODT texture hashes, MODS alternate textures, and the DEST destruction block.
    ///     <para>
    ///         Separate from <see cref="ReadGenericRecord" /> because these three live on
    ///         <b>every</b> type that inherits the corresponding engine base class, including the
    ///         ~20 types routed to hand-written specialized readers that never call the generic
    ///         reader at all. WEAP, ARMO, STAT, MISC, DOOR, NPC_ and CREA all carry them and all
    ///         bypass the generic path, so without this entry point the payloads stay invisible on
    ///         exactly the types most worth browsing.
    ///     </para>
    ///     <para>
    ///         Returns null when this record's layout carries none of the three, so a caller can
    ///         skip the struct read entirely for the majority of FormTypes.
    ///     </para>
    /// </summary>
    public RuntimeNestedPayloads? ReadNestedPayloads(RuntimeEditorIdEntry entry)
    {
        var layout = PdbStructLayouts.Get(entry.FormType);
        if (layout == null || !PdbStructLayouts.CarriesNestedPayload(entry.FormType))
        {
            return null;
        }

        if (ResolveStruct(entry, layout) is not var (structData, _, shift))
        {
            return null;
        }

        var textureHashes = ReadNestedField(
            layout, structData, shift, "TESModel", "TextureList") as RuntimeTextureHashList;
        var alternateTextures = ReadNestedField(
            layout, structData, shift, "TESModelTextureSwap", "TextureSwapList")
            as IReadOnlyList<AlternateTextureEntry>;
        var destruction = ReadNestedField(
            layout, structData, shift, "BGSDestructibleObjectForm", "pData") as DestructionData;

        if (textureHashes == null && alternateTextures == null && destruction == null)
        {
            return null;
        }

        return new RuntimeNestedPayloads(textureHashes, alternateTextures, destruction);
    }

    private object? ReadNestedField(
        PdbTypeLayout layout, byte[] structData, int shift, string owner, string name)
    {
        var field = layout.Fields.FirstOrDefault(f => f.Owner == owner && f.Name == name);
        if (field == null)
        {
            return null;
        }

        var offset = ApplyFieldShift(field, shift);
        if (offset < 0 || offset + field.Size > structData.Length)
        {
            return null;
        }

        return RuntimeContainerFieldReader.Read(_context, structData, field, offset, layout.Fields);
    }

    /// <summary>
    ///     Locate and read a record's complete-object struct, applying the interior-base correction
    ///     and the per-type / per-record layout shift. Shared by every entry point so they cannot
    ///     drift apart on the one piece of logic that is genuinely subtle here.
    /// </summary>
    private (byte[] Data, long ObjectBase, int Shift)? ResolveStruct(
        RuntimeEditorIdEntry entry, PdbTypeLayout layout)
    {
        if (!entry.TesFormOffset.HasValue)
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
        var shift = _typeShifts.TryGetValue(entry.FormType, out var s) ? s : 0;
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

        return (structData, objectBase, correctedShift);
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
            var value = ReadFieldValue(structData, field, tesFormFileOffset, effectiveOffset, fields);
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
        // Find a BSStringT field to use as a validator (prefer cModel — higher success rate).
        // Falling back to ANY BSStringT matters: a type with neither cModel nor cFullName —
        // LSCR, GLOB, LSCT and friends — otherwise gets no per-record correction at all, because
        // the two named members are the only validators this ever looked for. LSCR carries
        // TESTexture.TextureName and TESLoadScreen.cDescText, either of which validates fine.
        var probeField = layout.Fields.FirstOrDefault(f => f is { Name: "cModel", Owner: "TESModel" })
                         ?? layout.Fields.FirstOrDefault(f => f is { Name: "cFullName", Owner: "TESFullName" })
                         ?? layout.Fields.FirstOrDefault(f =>
                             f.Kind == "struct" && f.TypeDetail == "BSStringT<char>" && f.Owner != "TESForm");
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

                var probe = GetFieldProbe(field);

                if (probe == null)
                {
                    continue; // Only probe fields with strong validation signals
                }

                fieldSpecs.Add(new RuntimeReaderFieldProbe.FieldSpec(
                    field.Name, field.Offset, 1, probe.Value.Check, CheckArg: probe.Value.Arg));
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
        return GetFieldProbe(field)?.Check;
    }

    /// <summary>
    ///     Pick the probe check for a field, with the argument it needs.
    ///     <para>
    ///         A check that can never pass is not free: <c>ScoreSample</c> adds every declared field
    ///         to the denominator and only a passing one to the numerator, so a check that fails at
    ///         <i>every</i> candidate shift dilutes the margin the caller gates on
    ///         (<c>Margin &gt;= 2</c>) and makes a real layout shift harder to detect. Fields
    ///         therefore get a check only when it is discriminating for that field's actual shape.
    ///     </para>
    ///     <para>
    ///         That is why a <c>BSSimpleList&lt;TEX_SWAP *&gt;</c> or a <c>TESTextureList</c> gets
    ///         none even though the reader now walks both: the first word of a TESTextureList is a
    ///         count, and a TEX_SWAP node is a plain allocation, so neither head is a form pointer
    ///         and no available check would do anything but add noise.
    ///     </para>
    /// </summary>
    internal static (RuntimeReaderFieldProbe.FieldCheck Check, object? Arg)? GetFieldProbe(
        PdbFieldLayout field)
    {
        if (field.Kind is "float32" or "float")
        {
            return (RuntimeReaderFieldProbe.FieldCheck.NormalFloat, null);
        }

        if (field.Kind == "struct")
        {
            if (field.TypeDetail is "BSStringT<char>")
            {
                return (RuntimeReaderFieldProbe.FieldCheck.BSStringT, null);
            }

            // A BSSimpleList head's first word points at the first item, so when the element type
            // is a record class the strongest available check applies — and unlike a bare
            // PointerToForm it also rejects a pointer that lands on a form of the wrong type.
            return TryGetListElementFormType(field.TypeDetail) is { } listFormType
                ? (RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, listFormType)
                : null;
        }

        if (field.Kind != "pointer")
        {
            return null;
        }

        // A pointer whose target the layout database knows is not a record class — a
        // DestructibleObjectData allocation, a BaseProcess, an NiNode — can never resolve to a
        // TESForm, so PointerToForm on it was pure dilution on every type carrying one.
        if (field.TypeDetail is not { Length: > 0 } target)
        {
            return (RuntimeReaderFieldProbe.FieldCheck.PointerToForm, null);
        }

        if (PdbStructLayouts.TryGetFormTypeByClassName(target, out var formType))
        {
            return (RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, formType);
        }

        return PdbStructLayouts.TryGetAuxStruct(target, out _)
            ? null
            : (RuntimeReaderFieldProbe.FieldCheck.PointerToForm, null);
    }

    /// <summary>
    ///     FormType of a <c>BSSimpleList&lt;X *&gt;</c>'s element class, when X is a record class.
    /// </summary>
    private static byte? TryGetListElementFormType(string? typeDetail)
    {
        if (typeDetail is null || !typeDetail.StartsWith("BSSimpleList<", StringComparison.Ordinal))
        {
            return null;
        }

        var open = typeDetail.IndexOf('<', StringComparison.Ordinal);
        var close = typeDetail.LastIndexOf('>');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var element = typeDetail[(open + 1)..close].Trim().TrimEnd('*').Trim();
        return PdbStructLayouts.TryGetFormTypeByClassName(element, out var formType) ? formType : null;
    }

    /// <summary>
    ///     Read a single field value from the struct data based on its PDB type kind.
    /// </summary>
    internal object? ReadFieldValue(byte[] data, PdbFieldLayout field, long tesFormFileOffset,
        int effectiveOffset = -1, IReadOnlyList<PdbFieldLayout>? siblingFields = null)
    {
        var offset = effectiveOffset >= 0 ? effectiveOffset : field.Offset;

        // Containers are checked before the Kind switch because they do not have a Kind of their
        // own: a BSSimpleList arrives as kind:"struct" and a counted pointer array as
        // kind:"pointer", so both would otherwise be handled as the scalar they are shaped like.
        if (RuntimeContainerFieldReader.Handles(field))
        {
            var container = RuntimeContainerFieldReader.Read(
                _context, data, field, offset, siblingFields ?? []);
            if (container != null)
            {
                return container;
            }

            // An unresolvable container falls through: a BSSimpleList head is still an 8-byte
            // struct and a counted array is still a pointer, so the existing arms keep whatever
            // diagnostic value the raw value had.
        }

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
            // An array the container reader did not claim has no typed walk, but it still has
            // bytes. Without this arm the switch fell through to null and the field vanished
            // silently — the same shape of loss as the MODT all-or-nothing bail. 43 of the
            // layout's 54 array fields land here, including RACE's head/body model and texture
            // lists, NPC_ FaceGen offsets, WTHR colour data and the ARMO/ARMA/CLOT biped models.
            // Raw bytes are what ReadEmbeddedStruct already hands back for a struct over 8 bytes,
            // and ShowHelpers renders them through the same hex preview.
            "array" => ReadRawArray(data, field, offset),
            _ => null
        };
    }

    /// <summary>
    ///     Return an unclaimed array field's raw bytes, or null when it is entirely zero.
    ///     <para>
    ///         An all-zero array is an allocation the engine never populated, and reporting it would
    ///         put a page of "00" in front of a reader for every unset field. A non-zero one is real
    ///         captured content whose typed decode simply does not exist yet.
    ///     </para>
    /// </summary>
    private static byte[]? ReadRawArray(byte[] data, PdbFieldLayout field, int offset)
    {
        var span = data.AsSpan(offset, field.Size);
        return span.IndexOfAnyExcept((byte)0) < 0 ? null : span.ToArray();
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

        // For BSStringT pointers (cFullName, cModel, etc.) — skip, handled separately
        if (field.TypeDetail is "BSStringT<char>")
        {
            return null;
        }

        // When the layout names the target's class, DEMAND that FormType.
        //
        // The untyped follow is far weaker than it looks: it accepts any pointer into captured
        // memory whose byte at +4 is <= 200 and whose word at +12 is non-zero. Every ASCII
        // character satisfies the first and most text satisfies the second, so a stale pointer
        // landing in a string returns that string's bytes as a "FormID". LSCR.pLoadScreenType is
        // the worked example — it reported 0x20736B69, which is the ASCII " ski". The layout has
        // said "TESLoadScreenType" all along; nothing was using it.
        // The demanded set is the declared class PLUS every record class deriving from it, because
        // C++ pointer assignment is covariant: TESObjectREFR* pShooter holds a Character (ACHR) or
        // Creature (ACRE) in practice and a bare REFR almost never, so insisting on the declared
        // class alone would reject the correct answer on 15 of the 248 typed fields.
        if (field.TypeDetail is { Length: > 0 } target)
        {
            if (PdbStructLayouts.TryGetAssignableFormTypes(target, out var acceptableFormTypes))
            {
                // Null rather than the raw word: a pointer declared as a record class that does not
                // resolve to one is a misread, and reporting the word would make it
                // indistinguishable from a recovered reference.
                return _context.FollowPointerToFormId(data, effectiveOffset, acceptableFormTypes);
            }

            // The layout names a target class and it is NOT a record class — an aux struct, a
            // template container, a plain allocation. The weak untyped follow below would happily
            // read that allocation's bytes and hand them back as a "FormID" (the " ski" failure
            // shape); a declared non-form pointer gets the raw VA as a diagnostic instead.
            return _context.IsValidPointer(va) ? va : null;
        }

        // Try to follow as a TESForm pointer and get the target's FormID
        var formId = _context.FollowPointerToFormId(data, effectiveOffset);
        if (formId.HasValue)
        {
            return formId.Value;
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

        // A TESTexture MEMBER (12 bytes: vtable + BSStringT). The layout database never exports
        // nested struct layouts, but it does not have to here: 28 types carry TESTexture as a BASE
        // class, and in every one of them the flattened `TESTexture.TextureName` BSStringT sits
        // exactly 4 bytes past where the base begins (LSCR: TESForm ends at 40, TextureName @44;
        // MICN, CHIP, WEAP, … all agree). So the string is at +4 within the member too.
        if (field.TypeDetail is "TESTexture" && field.Size == 12)
        {
            return _context.ReadBSStringTDiag(data, effectiveOffset + 4, out _);
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
