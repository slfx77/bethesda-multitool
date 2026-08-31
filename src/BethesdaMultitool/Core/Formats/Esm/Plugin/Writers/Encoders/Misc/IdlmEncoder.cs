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
///         IDLA comes from <c>BGSIdleCollection.pIdleArray</c> @72 — a pointer to an out-of-struct
///         array of <c>cIdleCount</c> IDLE pointers. <c>RuntimeContainerFieldReader</c> walks it and
///         resolves each element through the TESForm header check, all-or-nothing. Because
///         <c>wbIdleAnimation</c> drives IDLA's element count from IDLC, the two must agree: IDLC is
///         written as the resolved array's length, or as <b>0</b> with no IDLA when the array could
///         not be walked. It is never written as a count with nothing behind it.
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

        // IDLC and IDLA are a matched pair — see the class remarks. The walked array is the
        // authority for both: its length becomes IDLC, so the two can never disagree.
        var idleAnimations = GenericRecordFields.TryFormIdList(idlm, "IDLA", "BGSIdleCollection.pIdleArray");
        var capturedCount = GenericRecordFields.TryUInt(idlm, "IDLC", "BGSIdleCollection.cIdleCount") ?? 0;

        if (idleAnimations is { Count: > 0 } animations)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLC", (byte)Math.Min(animations.Count, byte.MaxValue)));
            subs.Add(NewRecordSubrecords.EncodeFormIdArraySubrecord("IDLA", animations));
        }
        else
        {
            if (capturedCount > 0)
            {
                warnings.Add(
                    $"IDLM 0x{idlm.FormId:X8} runtime declares {capturedCount} idle animation(s) but " +
                    "pIdleArray did not resolve to that many IDLE forms — emitting IDLC as 0 and no IDLA.");
            }

            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLC", 0));
        }

        if (GenericRecordFields.TryFloat(idlm, "IDLT", "BGSIdleCollection.fTimerCheckForIdle") is { } timer)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("IDLT", timer));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
