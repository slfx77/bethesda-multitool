using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Acoustic Space (ASPC) record — 64-79 forms in every captured dump. CELL records
///     already emit an <c>XCAS</c> FormID pointing at an acoustic space, so without this encoder a
///     proto-only ASPC leaves that XCAS dangling.
///     <para>
///         Canonical order from xEdit <c>wbRecord(ASPC)</c> (wbDefinitionsFNV.pas):
///         EDID(req), OBND(req), SNAM ×5(req), WNAM(req), RDAT?, ANAM(req), INAM(req).
///     </para>
///     <para>
///         The five SNAMs are <b>positional</b> — Dawn/Default, Afternoon, Dusk, Night, Walla — and are
///         marked required with NULL permitted. They must therefore all be emitted, in order, even
///         when a slot is null; dropping an empty one would silently shift every later sound into the
///         wrong time-of-day slot.
///     </para>
/// </summary>
public sealed class AspcEncoder : IRecordEncoder
{
    /// <summary>
    ///     Highest valid ANAM environment type. xEdit's FNV enum has 31 entries, so the top index
    ///     is 0x1E; the previous 0x40 bound was looser than the schema it was guarding.
    /// </summary>
    private const uint MaxEnvironmentType = 0x1E;

    /// <summary>The five positional SNAM slots, in xEdit order, with their PDB field names.</summary>
    private static readonly string[] SoundFields =
    [
        "BGSAcousticSpace.pDawnSound",
        "BGSAcousticSpace.pNoonSound",
        "BGSAcousticSpace.pDuskSound",
        "BGSAcousticSpace.pNightSound",
        "BGSAcousticSpace.pWallaSound"
    ];

    public string RecordType => "ASPC";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord aspc)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(aspc.EditorId))
        {
            warnings.Add($"New ASPC 0x{aspc.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", aspc.EditorId ?? string.Empty));

        if (aspc.Bounds is not null && GenericRecordFields.IsPlausibleBounds(aspc.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(aspc.Bounds));
        }
        else if (aspc.Bounds is not null)
        {
            warnings.Add($"ASPC 0x{aspc.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        // Positional and required — emit all five, using 0 (NULL, which the schema permits) for
        // any slot the capture did not resolve, so the remaining slots keep their meaning.
        // Slot 0 also accepts a bare "SNAM": records arriving via the ESM-carve path key their
        // fields by signature, and Fallout 3's ASPC has exactly one SNAM ('Sound - Looping') whose
        // FNV counterpart is this slot ('Dawn / Default Loop'). Only slot 0 takes that fallback —
        // applying it to all five would clone one sound across every time of day.
        for (var slot = 0; slot < SoundFields.Length; slot++)
        {
            var sound = (slot == 0
                ? GenericRecordFields.TryFormId(aspc, SoundFields[0], "SNAM")
                : GenericRecordFields.TryFormId(aspc, SoundFields[slot])) ?? 0u;
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", sound));
        }

        // The three required scalars are tiny by schema: WNAM is a walla trigger count, ANAM an
        // environment-type enum (xEdit's list tops out at index 0x1E), INAM a bool. These guards
        // are the second line of defence — RuntimeAcousticSpaceReader now picks the captured
        // build's real layout and type-validates every pointer — but they stay because the
        // ESM-carve path reaches this encoder without going through that reader, and because a
        // misaligned read shows up here as a heap POINTER (v138 emitted ANAM=0x40C61A00-class
        // values). Out-of-range ⇒ treat as uncaptured: emit the required subrecord as 0 and say so.
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord(
            "WNAM", SmallScalarOrZero(aspc, "WNAM", 0xFFFF, warnings, "WNAM walla count",
                "BGSAcousticSpace.iWallaPop")));

        if (GenericRecordFields.TryFormId(aspc, "RDAT", "BGSAcousticSpace.pSoundRegion") is { } region)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RDAT", region));
        }

        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord(
            "ANAM", SmallScalarOrZero(aspc, "ANAM", MaxEnvironmentType, warnings, "ANAM environment type",
                "BGSAcousticSpace.eEnvType")));

        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord(
            "INAM", SmallScalarOrZero(aspc, "INAM", 1, warnings, "INAM is-interior",
                "BGSAcousticSpace.bIsInterior")));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Read a required small scalar, treating anything above <paramref name="max" /> as a
    ///     misaligned capture (pointer bytes) rather than data.
    /// </summary>
    private static uint SmallScalarOrZero(
        GenericEsmRecord aspc, string signature, uint max, List<string> warnings,
        string description, params string[] keys)
    {
        var value = GenericRecordFields.TryUInt(aspc, [signature, .. keys]);
        if (value is null)
        {
            return 0;
        }

        if (value.Value > max)
        {
            warnings.Add(
                $"ASPC 0x{aspc.FormId:X8} {description} value 0x{value.Value:X8} exceeds the " +
                $"plausible range (≤{max}) — misaligned capture; emitting 0.");
            return 0;
        }

        return value.Value;
    }
}
