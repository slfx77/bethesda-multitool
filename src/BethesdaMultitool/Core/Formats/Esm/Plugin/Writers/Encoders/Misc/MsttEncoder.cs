using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Moveable Static (MSTT) record. MSTT is the largest single emission gap in the
///     captured corpus — 298-323 forms in every dump — and is decoded on every DMP load into
///     <c>RecordCollection.GenericRecords</c>, then dropped at the writer boundary. Missing the
///     encoder strips every proto-only moveable static, so any REFR placed on one loses its base
///     object.
///     <para>
///         MSTT has no dedicated typed model; like FLOR it arrives as a
///         <see cref="GenericEsmRecord" /> produced by <c>RuntimeGenericReader</c>, which keys
///         <see cref="GenericEsmRecord.Fields" /> by PDB identifier — hence the
///         <see cref="GenericRecordFields" /> lookups.
///     </para>
///     <para>
///         Canonical order from xEdit <c>wbRecord(MSTT)</c> (wbDefinitionsFNV.pas):
///         EDID(req), OBND(req), FULL?, MODL(req), DEST?, DATA(req, u8), SNAM?.
///     </para>
/// </summary>
public sealed class MsttEncoder : IRecordEncoder
{
    public string RecordType => "MSTT";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord mstt)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(mstt.EditorId))
        {
            warnings.Add($"New MSTT 0x{mstt.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", mstt.EditorId ?? string.Empty));

        if (mstt.Bounds is not null && GenericRecordFields.IsPlausibleBounds(mstt.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(mstt.Bounds));
        }
        else if (mstt.Bounds is not null)
        {
            warnings.Add($"MSTT 0x{mstt.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        if (!string.IsNullOrEmpty(mstt.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", mstt.FullName));
        }

        if (!string.IsNullOrEmpty(mstt.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", mstt.ModelPath));
        }

        // DEST is deliberately not emitted. BGSDestructibleObjectForm.pData is a pointer to a
        // separate DestructibleObjectData allocation; the generic reader resolves pointers to a
        // FormID or a raw VA and never walks that struct, so there is no captured destruction
        // data to write. Emitting a synthesized DEST would invent content.

        // DATA is `.SetRequired` in xEdit — a single u8 "On Local Map" boolean, held at runtime
        // in the 1-byte BGSMovableStatic.data (MOVABLE_STATIC_DATA @128), which the generic
        // reader surfaces as a hex string. Fall back to 0 (not on local map) so the record still
        // loads when the byte was not recovered, and say so.
        var data = GenericRecordFields.TryBytes(mstt, 1, "DATA", "BGSMovableStatic.data");
        if (data is null)
        {
            warnings.Add(
                $"MSTT 0x{mstt.FormId:X8} has no captured MOVABLE_STATIC_DATA — " +
                "emitting required DATA as 0 (not on local map).");
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", data?[0] ?? 0));

        // SNAM is the moveable static's own looping sound (BGSMovableStatic.pSoundLoop @124).
        // TESObjectSTAT.pRandomSound @120 is inherited from the STAT base and has no MSTT
        // subrecord in the FNV schema, so it is intentionally not emitted here.
        if (GenericRecordFields.TryFormId(mstt, "SNAM", "BGSMovableStatic.pSoundLoop") is { } sound)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", sound));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
