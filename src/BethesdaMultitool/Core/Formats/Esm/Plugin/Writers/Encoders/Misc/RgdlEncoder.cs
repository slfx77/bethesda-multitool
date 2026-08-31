using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Ragdoll (RGDL) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(RGDL)</c> (wbDefinitionsFNV.pas):
///         EDID(req), NVER(req), DATA(req, 14 bytes), XNAM(req), TNAM(req), RAFD, RAFB, RAPS(req),
///         ANAM.
///     </para>
///     <para>
///         PDB <c>BGSRagdoll</c> (size 344): <c>SaveStruct</c> @64 (14-byte
///         <c>RagdollSaveStruct</c>) → DATA — the size matches xEdit's General Data byte-for-byte
///         (u32 dynamic bone count + 4 unused + five bool flags + 1 unused) and a 14-byte RGDL DATA
///         schema is already registered. <c>pPreviewActor</c> @340 → XNAM (a CREA/NPC_),
///         <c>pBodyPartData</c> @336 → TNAM (a BPTD).
///     </para>
///     <para>
///         <b>What is deliberately not emitted, and why.</b> The remaining runtime blocks are larger
///         than their file counterparts, so their bytes are not a prefix that can be sliced without
///         the nested member layout the PDB export does not carry: <c>FeedbackData</c> is 80 bytes
///         against RAFD's 60, and <c>PoseMatchingData</c> is 36 against RAPS's 24. Writing either
///         would be reinterpretation, not recovery. NVER is a plugin-format version that exists
///         nowhere in the runtime object. <c>cModel</c>/<c>TextureList</c>/<c>cFlags</c> come from
///         the shared <c>TESModel</c> base and have no RGDL subrecord at all.
///     </para>
/// </summary>
public sealed class RgdlEncoder : IRecordEncoder
{
    /// <summary>Runtime <c>RagdollSaveStruct</c> size — identical to xEdit's General Data block.</summary>
    private const int RagdollSaveStructSize = 14;

    /// <summary>
    ///     Sanity ceiling on 'Dynamic Bone Count'. Retail FNV ragdolls sit in the low tens; a value
    ///     past this is a misaligned capture, not data.
    /// </summary>
    private const uint MaxDynamicBoneCount = 255;

    public string RecordType => "RGDL";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord rgdl)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(rgdl.EditorId))
        {
            warnings.Add($"New RGDL 0x{rgdl.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", rgdl.EditorId ?? string.Empty));

        var data = EncodeData(rgdl);
        if (data is null)
        {
            warnings.Add($"RGDL 0x{rgdl.FormId:X8} has no convertible RagdollSaveStruct — omitting required DATA.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DATA", data));
        }

        if (GenericRecordFields.TryFormId(rgdl, "XNAM", "BGSRagdoll.pPreviewActor") is { } actorBase)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("XNAM", actorBase));
        }
        else
        {
            warnings.Add($"RGDL 0x{rgdl.FormId:X8} preview actor did not resolve — omitting required XNAM.");
        }

        if (GenericRecordFields.TryFormId(rgdl, "TNAM", "BGSRagdoll.pBodyPartData") is { } bodyPartData)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM", bodyPartData));
        }
        else
        {
            warnings.Add($"RGDL 0x{rgdl.FormId:X8} body part data did not resolve — omitting required TNAM.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Produce PC little-endian DATA bytes.
    ///     <para>
    ///         ⚠ The runtime block does <b>not</b> go through
    ///         <c>SubrecordSchemaProcessor.ConvertWithSchema</c>, unlike every other encoder in this
    ///         family. The registered 14-byte RGDL DATA schema declares its leading count as
    ///         <c>UInt32WordSwapped</c> — a quirk of what the Xbox 360 <i>plugin writer</i> put on
    ///         disk, established by comparing Xbox and PC ESMs. A compiler-laid-out
    ///         <c>RagdollSaveStruct</c> in memory is a plain big-endian u32, so running runtime
    ///         bytes through the file schema would swap the halves of a value that was never
    ///         swapped. The struct is one integer, five booleans and five unused bytes, so it is
    ///         written out directly instead. The carve path still uses the schema, where it is
    ///         ground truth.
    ///     </para>
    /// </summary>
    private static byte[]? EncodeData(GenericEsmRecord rgdl)
    {
        if (GenericRecordFields.TryBytes(rgdl, RagdollSaveStructSize, "DATA", "BGSRagdoll.SaveStruct") is { } raw)
        {
            var boneCount = BinaryPrimitives.ReadUInt32BigEndian(raw);
            if (boneCount > MaxDynamicBoneCount)
            {
                return null; // Misaligned capture, not a ragdoll with four million bones.
            }

            var data = new byte[RagdollSaveStructSize];
            BinaryPrimitives.WriteUInt32LittleEndian(data, boneCount);
            // Bytes 4-7 are unused; 8-12 are the five one-byte 'Enabled' booleans, which have no
            // endianness; 13 is unused.
            raw.AsSpan(8, 5).CopyTo(data.AsSpan(8));
            return data;
        }

        if (rgdl.Fields.TryGetValue("DATA", out var value)
            && value is IReadOnlyDictionary<string, object?> { Count: > 0 } decoded)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "RGDL", RagdollSaveStructSize);
            return schema is null ? null : SchemaDictionarySerializer.Serialize(schema, decoded);
        }

        return null;
    }
}
