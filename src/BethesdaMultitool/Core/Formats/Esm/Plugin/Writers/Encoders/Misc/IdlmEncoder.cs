using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Idle Marker (IDLM) record. No typed model — it reaches the writer as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(IDLM)</c> → <c>wbIdleAnimation</c>
///         (wbDefinitionsCommon.pas): EDID(req), OBND(req), then the 'Idle Animations' struct —
///         IDLF(u8 flags), IDLC(u8 count), IDLT(float timer), IDLA(FormID array), IDLB. Every
///         member of that struct is optional in the FNV branch.
///     </para>
///     <para>
///         PDB <c>BGSIdleMarker</c> (size 80): <c>BoundData</c> @52 → OBND,
///         <c>BGSIdleCollection.cIdleFlags</c> @68 → IDLF,
///         <c>BGSIdleCollection.cIdleCount</c> @69 → IDLC,
///         <c>BGSIdleCollection.fTimerCheckForIdle</c> @76 → IDLT.
///     </para>
///     <para>
///         IDLA is deliberately not emitted: <c>BGSIdleCollection.pIdleArray</c> @72 is a pointer
///         to an out-of-struct array of IDLE pointers that no reader walks. Because
///         <c>wbIdleAnimation</c> drives IDLA's element count from IDLC, emitting the runtime's
///         <c>cIdleCount</c> with no array behind it would tell the engine to read animations that
///         are not there — so IDLC is written as <b>0</b>, matching the absent array.
///     </para>
/// </summary>
public sealed class IdlmEncoder : IRecordEncoder
{
    /// <summary>
    ///     Highest plausible idle-flag byte. xEdit's flag list tops out at bit 4
    ///     ('Ignored By Sandbox'), so anything above 0x1F is a misaligned read rather than data.
    /// </summary>
    private const uint MaxIdleFlags = 0x1F;

    public string RecordType => "IDLM";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord idlm)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(idlm.EditorId))
        {
            warnings.Add($"New IDLM 0x{idlm.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", idlm.EditorId ?? string.Empty));

        if (idlm.Bounds is not null && GenericRecordFields.IsPlausibleBounds(idlm.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(idlm.Bounds));
        }
        else if (idlm.Bounds is not null)
        {
            warnings.Add($"IDLM 0x{idlm.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        var flags = GenericRecordFields.TryUInt(idlm, "IDLF", "BGSIdleCollection.cIdleFlags");
        if (flags > MaxIdleFlags)
        {
            warnings.Add(
                $"IDLM 0x{idlm.FormId:X8} idle flags 0x{flags.Value:X8} exceed the plausible range " +
                $"(≤0x{MaxIdleFlags:X2}) — misaligned capture; omitting IDLF.");
            flags = null;
        }

        if (flags is { } idleFlags)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLF", (byte)idleFlags));
        }

        // Forced to zero — see the class remarks. Warn only when animations were actually
        // captured and are being dropped, so the diagnostic marks real data loss.
        var capturedCount = GenericRecordFields.TryUInt(idlm, "IDLC", "BGSIdleCollection.cIdleCount") ?? 0;
        if (capturedCount > 0)
        {
            warnings.Add(
                $"IDLM 0x{idlm.FormId:X8} runtime holds {capturedCount} idle animation(s) behind " +
                "pIdleArray, which is not walked — emitting IDLC as 0 and no IDLA.");
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLC", 0));

        if (GenericRecordFields.TryFloat(idlm, "IDLT", "BGSIdleCollection.fTimerCheckForIdle") is { } timer)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("IDLT", timer));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
