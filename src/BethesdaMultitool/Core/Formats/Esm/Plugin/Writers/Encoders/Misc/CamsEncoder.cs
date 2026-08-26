using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Camera Shot (CAMS) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(CAMS)</c> (wbDefinitionsFNV.pas):
///         EDID(req), model, DATA(req, 40 bytes), MNAM?.
///     </para>
///     <para>
///         PDB <c>BGSCameraShot</c> (size 136): <c>cModel</c> @44 → MODL,
///         <c>pFormImageSpaceModifying</c> @68 → MNAM (an IMAD),
///         <c>Data</c> @72 → DATA (the 40-byte <c>CAMERA_SHOT_DATA</c>).
///     </para>
///     <para>
///         <b>Endianness.</b> The runtime bytes are big-endian and DATA is a mixed block of four
///         u32 enums/flags followed by six floats, so it must not be hand-swapped.
///         <see cref="SubrecordSchemaProcessor.ConvertWithSchema" /> is the single oracle for
///         BE→LE subrecord conversion and already carries CAMS DATA schemas at both observed sizes
///         (40 bytes, and the 36-byte form without 'Target % Between Actors'). When it declines —
///         no schema for the captured length — the record is emitted without DATA rather than with
///         bytes in the wrong order.
///     </para>
/// </summary>
public sealed class CamsEncoder : IRecordEncoder
{
    /// <summary>Full <c>CAMERA_SHOT_DATA</c> size: 4×u32 + 6×float.</summary>
    private const int CameraShotDataSize = 40;

    public string RecordType => "CAMS";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord cams)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cams.EditorId))
        {
            warnings.Add($"New CAMS 0x{cams.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cams.EditorId ?? string.Empty));

        // CAMS has no OBND and no FULL in the FNV schema — BGSCameraShot is not a TESBoundObject
        // and carries no TESFullName — so neither is emitted even when the carrier holds one.
        if (!string.IsNullOrEmpty(cams.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", cams.ModelPath));
        }

        var data = EncodeData(cams);
        if (data is null)
        {
            warnings.Add(
                $"CAMS 0x{cams.FormId:X8} has no convertible CAMERA_SHOT_DATA — omitting required DATA.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DATA", data));
        }

        if (GenericRecordFields.TryFormId(cams, "MNAM", "TESImageSpaceModifiableForm.pFormImageSpaceModifying")
            is { } imageSpaceModifier)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("MNAM", imageSpaceModifier));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Produce PC little-endian DATA bytes from whichever shape the producer stored.
    ///     The runtime reader yields the raw 40 big-endian bytes; the ESM carve path runs the
    ///     subrecord through the same registered schema and stores the decoded field dictionary
    ///     instead, so both are re-serialized through the schema rather than reinterpreted here.
    /// </summary>
    private static byte[]? EncodeData(GenericEsmRecord cams)
    {
        if (GenericRecordFields.TryBytes(cams, CameraShotDataSize, "DATA", "BGSCameraShot.Data") is { } raw)
        {
            return SubrecordSchemaProcessor.ConvertWithSchema("DATA", raw, "CAMS");
        }

        if (cams.Fields.TryGetValue("DATA", out var value)
            && value is IReadOnlyDictionary<string, object?> { Count: > 0 } decoded)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "CAMS", CameraShotDataSize);
            return schema is null ? null : SchemaDictionarySerializer.Serialize(schema, decoded);
        }

        return null;
    }
}
