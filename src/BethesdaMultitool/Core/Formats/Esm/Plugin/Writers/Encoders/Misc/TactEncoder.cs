using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Talking Activator (TACT) record — 64-74 forms in every captured dump. TACT is the
///     base form behind radio stations and talking terminals; missing the encoder strips proto-only
///     ones, so any REFR placed on them loses its base object and the radio it broadcast.
///     <para>
///     Canonical order from xEdit <c>wbRecord(TACT)</c> (wbDefinitionsFNV.pas):
///     EDID(req), OBND(req), FULL?, MODL(req), SCRI?, DEST?, SNAM?, VNAM?, INAM?.
///     </para>
/// </summary>
public sealed class TactEncoder : IRecordEncoder
{
    public string RecordType => "TACT";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord tact)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(tact.EditorId))
        {
            warnings.Add($"New TACT 0x{tact.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", tact.EditorId ?? string.Empty));

        if (tact.Bounds is not null && GenericRecordFields.IsPlausibleBounds(tact.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(tact.Bounds));
        }
        else if (tact.Bounds is not null)
        {
            warnings.Add($"TACT 0x{tact.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        if (!string.IsNullOrEmpty(tact.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", tact.FullName));
        }

        if (!string.IsNullOrEmpty(tact.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", tact.ModelPath));
        }

        if (GenericRecordFields.TryFormId(tact, "SCRI", "TESScriptableForm.pFormScript") is { } script)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", script));
        }

        // DEST omitted for the same reason as MSTT: BGSDestructibleObjectForm.pData points at a
        // separate allocation the generic reader never walks, so no destruction data was captured.

        // SNAM is the looping sound. Note TESObjectACTI.pSoundActivate (@136) has no TACT
        // subrecord in the FNV schema — only ACTI carries an activation sound — so it is not
        // emitted here even though the runtime struct inherits the field.
        if (GenericRecordFields.TryFormId(tact, "SNAM", "TESObjectACTI.pSoundLoop") is { } loop)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", loop));
        }

        if (GenericRecordFields.TryFormId(tact, "VNAM", "BGSTalkingActivator.pVoiceType") is { } voice)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("VNAM", voice));
        }

        if (GenericRecordFields.TryFormId(tact, "INAM", "TESObjectACTI.pRadioTemplate") is { } radio)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", radio));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
