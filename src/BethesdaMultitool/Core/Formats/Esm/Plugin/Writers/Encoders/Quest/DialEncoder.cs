using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="DialogTopicRecord" /> (DIAL) as PC-format subrecord bytes.
///     Emits the full record from scratch in xEdit-canonical order:
///     EDID + QSTI* + FULL? + PNAM (required) + DATA (last).
///     Override path is a no-op — DIAL definitions don't mutate at runtime.
///     DATA (2 bytes): byte TopicType(0) + byte Flags(1) — raw bytes, no endian swap.
///     PNAM (4 bytes): float Priority — topic ordering weight, GECK default 50.
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
    ///     GECK default topic priority; PNAM is a REQUIRED subrecord per xEdit
    ///     (wbFloat(PNAM).SetDefaultNativeValue(50).SetRequired).
    /// </summary>
    private const float DefaultPriority = 50f;

    /// <summary>
    ///     Encode a new DIAL record from scratch in the xEdit-canonical FNV order:
    ///     EDID, QSTI*, FULL?, PNAM (required), DATA — DATA is the LAST subrecord.
    ///     The engine's DIAL reader is strictly sequential; the previous order
    ///     (FULL before QSTI, DATA before PNAM) made FNVEdit flag every emitted topic
    ///     as "unexpected (or out of order) subrecord" and broke in-game topic
    ///     selection (wrong greetings / silent topics). FNV DIAL has no TNAM — a
    ///     captured speaker link is dropped with a warning.
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

        if (dial.QuestFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QSTI", dial.QuestFormId.Value));
        }

        if (!string.IsNullOrEmpty(dial.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", dial.FullName));
        }

        var priority = Math.Abs(dial.Priority) > float.Epsilon ? dial.Priority : DefaultPriority;
        subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("PNAM", priority));

        var data = new byte[2];
        data[0] = dial.TopicType;
        data[1] = dial.Flags;
        subs.Add(new EncodedSubrecord("DATA", data));

        if (dial.SpeakerFormId.HasValue)
        {
            warnings.Add(
                $"DIAL 0x{dial.FormId:X8} carries a speaker link 0x{dial.SpeakerFormId.Value:X8}; " +
                "FNV DIAL has no TNAM subrecord — dropped.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
