using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Media Set (MSET) record. No typed model — it reaches the writer as a
///     <see cref="GenericEsmRecord" />, produced at runtime by
///     <c>RuntimeMediaSetReader</c>, which keys <see cref="GenericEsmRecord.Fields" /> by subrecord
///     signature precisely so this encoder needs no MSET-specific model.
///     <para>
///         Canonical order from xEdit <c>wbRecord(MSET)</c> (wbDefinitionsFNV.pas):
///         EDID(req), FULL?, NAM1(u32 type), NAM2-NAM7 (six layer names),
///         NAM8/NAM9/NAM0/ANAM/BNAM/CNAM (six layer attenuations, dB),
///         JNAM/KNAM/LNAM/MNAM/NNAM/ONAM (six layer boundary percentages),
///         PNAM(u8 enable flags), DNAM/ENAM/FNAM/GNAM (four timings),
///         HNAM/INAM (intro/outro SOUN), DATA(unknown).
///     </para>
///     <para>
///         The three per-layer groups are <b>positional</b>: layer <i>n</i>'s dB belongs in the
///         <i>n</i>th dB signature. Each slot carries its own signature, so the order below IS the
///         mapping — a layer the capture could not read leaves its signatures out rather than
///         letting a later layer slide into them.
///     </para>
///     <para>
///         Trailing <c>wbUnknown(DATA)</c> is not emitted: it has no defined content, so there is
///         nothing to recover and nothing to synthesize.
///     </para>
/// </summary>
public sealed class MsetEncoder : IRecordEncoder
{
    /// <summary>The six layer-name signatures, in xEdit order (layer 0 → NAM2).</summary>
    private static readonly string[] LayerNames = ["NAM2", "NAM3", "NAM4", "NAM5", "NAM6", "NAM7"];

    /// <summary>The six layer-attenuation (dB) signatures, in xEdit order.</summary>
    private static readonly string[] LayerAttenuations = ["NAM8", "NAM9", "NAM0", "ANAM", "BNAM", "CNAM"];

    /// <summary>The six layer boundary-percentage signatures, in xEdit order.</summary>
    private static readonly string[] LayerPercents = ["JNAM", "KNAM", "LNAM", "MNAM", "NNAM", "ONAM"];

    /// <summary>The four trailing timing floats (fOne..fFour), in xEdit order.</summary>
    private static readonly string[] Timings = ["DNAM", "ENAM", "FNAM", "GNAM"];

    /// <summary>PDB field names for fOne..fFour.</summary>
    private static readonly string[] TimingFields =
    [
        "MediaSet.fOne", "MediaSet.fTwo", "MediaSet.fThree", "MediaSet.fFour"
    ];

    public string RecordType => "MSET";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord mset)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(mset.EditorId))
        {
            warnings.Add($"New MSET 0x{mset.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", mset.EditorId ?? string.Empty));

        // MediaSet is not a TESBoundObject, so there is no OBND in the schema even if the carrier
        // happens to hold bounds from another producer.
        if (!string.IsNullOrEmpty(mset.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", mset.FullName));
        }

        if (GenericRecordFields.TryUInt(mset, "NAM1", "MediaSet.Type") is { } setType)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM1", setType));
        }

        // The per-layer members live inside 16-byte MediaSet::MediaLayer structs, so there is no
        // flat PDB "Owner.Name" key for them — the whole point of RuntimeMediaSetReader is that it
        // walks those structs and publishes each member under its own subrecord signature. Layer
        // lookups are therefore signature-only, unlike the flat scalars below.
        foreach (var signature in LayerNames)
        {
            if (GenericRecordFields.TryString(mset, signature) is { } name)
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord(signature, name));
            }
        }

        foreach (var signature in LayerAttenuations)
        {
            AddFloat(subs, mset, signature);
        }

        foreach (var signature in LayerPercents)
        {
            AddFloat(subs, mset, signature);
        }

        if (GenericRecordFields.TryUInt(mset, "PNAM", "MediaSet.cEnableFlags") is { } enableFlags)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("PNAM", (byte)(enableFlags & 0xFF)));
        }

        for (var i = 0; i < Timings.Length; i++)
        {
            AddFloat(subs, mset, Timings[i], TimingFields[i]);
        }

        if (GenericRecordFields.TryFormId(mset, "HNAM", "MediaSet.pSoundOne") is { } introSound)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("HNAM", introSound));
        }

        if (GenericRecordFields.TryFormId(mset, "INAM", "MediaSet.pSoundTwo") is { } outroSound)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", outroSound));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Emit one 4-byte float slot, or nothing when the capture had no value for it.
    ///     <para>
    ///         The uint fallback is not a reinterpretation: every one of these MSET slots is
    ///         registered in <c>SubrecordCellAndMiscSchemas</c> as a <c>Simple4Byte</c>, so the ESM
    ///         carve path decodes the float's bit pattern into a boxed <see cref="uint" />. Writing
    ///         those four bytes little-endian produces exactly the bytes the float writer would.
    ///     </para>
    /// </summary>
    private static void AddFloat(
        List<EncodedSubrecord> subs, GenericEsmRecord mset, string signature, params string[] pdbFields)
    {
        string[] keys = [signature, .. pdbFields];

        if (GenericRecordFields.TryFloat(mset, keys) is { } value)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord(signature, value));
            return;
        }

        if (GenericRecordFields.TryUInt(mset, keys) is { } bits)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord(signature, bits));
        }
    }
}
