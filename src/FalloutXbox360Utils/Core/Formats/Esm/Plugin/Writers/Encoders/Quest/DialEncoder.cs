using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="DialogTopicRecord" /> (DIAL) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + FULL? + QSTI? + DATA + PNAM? + TNAM?.
///     Override path is a no-op — DIAL definitions don't mutate at runtime.
///     DATA (2 bytes): byte TopicType(0) + byte Flags(1) — raw bytes, no endian swap.
///     PNAM (4 bytes): float Priority — topic ordering weight.
///     <para>
///     <b>FormID-remap contract:</b> the encoder emits <c>dial.QuestFormId</c> (QSTI) and
///     <c>dial.SpeakerFormId</c> (TNAM) verbatim — no defensive
///     <c>FormIdReferenceResolver</c> call. It relies on
///     <c>DialogGrupBuilder.SanitizeDialReferences</c> patching the model upstream so
///     proto FormIDs are remapped to allocated ones before this method runs. Any new
///     caller that bypasses the sanitizer would emit phantom-master FormIDs; the systemic
///     regression guard in <c>PhantomMasterFormIdRegressionTests</c> catches that.
///     </para>
/// </summary>
public sealed class DialEncoder : IRecordEncoder
{
    public string RecordType => "DIAL";
    public Type ModelType => typeof(DialogTopicRecord);

    /// <summary>
    ///     Encode a new DIAL record from scratch in fopdoc canonical order:
    ///     EDID, FULL?, QSTI*, INFC?, INFX?, DATA, PNAM?, TNAM?.
    /// </summary>
    internal static EncodedRecord EncodeNew(DialogTopicRecord dial)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(dial.EditorId))
        {
            warnings.Add($"New DIAL 0x{dial.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", dial.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(dial.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", dial.FullName));
        }

        if (dial.QuestFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QSTI", dial.QuestFormId.Value));
        }

        var data = new byte[2];
        data[0] = dial.TopicType;
        data[1] = dial.Flags;
        subs.Add(new EncodedSubrecord("DATA", data));

        if (Math.Abs(dial.Priority) > float.Epsilon)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("PNAM", dial.Priority));
        }

        if (dial.SpeakerFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM", dial.SpeakerFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
