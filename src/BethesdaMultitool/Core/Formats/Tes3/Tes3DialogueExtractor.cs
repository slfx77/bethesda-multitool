using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     Builds the typed dialogue models (<see cref="DialogTopicRecord" /> / <see cref="DialogueRecord" />)
///     from Morrowind (TES3) DIAL and INFO records, so the Dialogue tab works for Morrowind.
///     <para>
///         TES3 dialogue is unlike the TES4 family in three ways handled here: (1) there is no GRUP — an
///         INFO belongs to the most recent DIAL in <b>file order</b>, so the caller threads the current
///         topic FormID positionally; (2) the response text is the INFO's <c>NAME</c> subrecord; (3) every
///         reference is a case-insensitive <b>editor-id string</b>, not a FormID — the speaker is
///         <c>ONAM</c> (an NPC_), with <c>FNAM</c> (faction) / <c>RNAM</c> (race) as the generic-dialogue
///         fallback. Those strings are resolved to the synthetic FormIDs the <see cref="Tes3RecordParser" />
///         assigns each record once the whole-file editor-id index is built (see
///         <see cref="ToRecord" />). TES3 plugins are little-endian and uncompressed.
///     </para>
/// </summary>
internal static class Tes3DialogueExtractor
{
    /// <summary>An INFO captured during the file-order pass, before editor-id → FormID resolution.</summary>
    public readonly record struct Tes3InfoDraft(
        uint FormId,
        string? EditorId,
        uint? TopicFormId,
        ushort InfoIndex,
        byte DialogType,
        string? ResponseText,
        string? Speaker,
        string? Faction,
        string? Race);

    /// <summary>Builds the topic model from a DIAL. The editor id (NAME) <i>is</i> the topic text.</summary>
    public static DialogTopicRecord BuildTopic(uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs)
    {
        byte dialogType = 0;
        foreach (var sub in subs)
        {
            if (sub.Signature == "DATA" && sub.Data.Length >= 1)
            {
                dialogType = sub.Data[0];
            }
        }

        return new DialogTopicRecord
        {
            FormId = formId,
            EditorId = editorId,
            FullName = editorId,
            TopicType = MapTopicType(dialogType)
        };
    }

    /// <summary>
    ///     Maps a TES3 dialog type (0 Topic, 1 Voice, 2 Greeting, 3 Persuasion, 4 Journal) onto the
    ///     model's <see cref="DialogTopicRecord.TopicTypeName" /> byte. Only Topic and Persuasion have a
    ///     model equivalent; Voice/Greeting/Journal are folded onto "Topic" so the FNV-era names never
    ///     render them as the actively-wrong "Conversation"/"Combat"/"Detection". The topic editor id
    ///     (e.g. "Greeting 5", journal quest ids) already conveys the finer kind.
    /// </summary>
    private static byte MapTopicType(byte dialogType) => dialogType == 3 ? (byte)3 : (byte)0;

    /// <summary>Captures an INFO's response + speaker strings, linked to the current topic by file order.</summary>
    public static Tes3InfoDraft BuildInfoDraft(
        uint formId, uint? topicFormId, ushort infoIndex, IReadOnlyList<RawSubrecord> subs)
    {
        string? editorId = null;
        string? response = null;
        string? speaker = null;
        string? faction = null;
        string? race = null;
        byte dialogType = 0;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "INAM":
                    editorId = ReadString(sub.Data);
                    break;
                case "NAME":
                    response = ReadString(sub.Data);
                    break;
                case "ONAM":
                    speaker = ReadString(sub.Data);
                    break;
                case "FNAM":
                    faction = ReadString(sub.Data);
                    break;
                case "RNAM":
                    race = ReadString(sub.Data);
                    break;
                // DATA: Dialog Type (u32) is byte 0 (values < 256, little-endian).
                case "DATA" when sub.Data.Length >= 1:
                    dialogType = sub.Data[0];
                    break;
            }
        }

        return new Tes3InfoDraft(formId, editorId, topicFormId, infoIndex, dialogType, response, speaker, faction, race);
    }

    /// <summary>
    ///     Finalizes an INFO draft into a <see cref="DialogueRecord" />, resolving the editor-id speaker
    ///     strings to the synthetic FormIDs in <paramref name="idToFormId" /> (built across the whole file).
    /// </summary>
    public static DialogueRecord ToRecord(
        Tes3InfoDraft draft, IReadOnlyDictionary<string, uint> idToFormId)
    {
        uint? Resolve(string? id) =>
            !string.IsNullOrEmpty(id) && idToFormId.TryGetValue(id, out var formId) ? formId : null;

        var responses = new List<DialogueResponse>();
        if (!string.IsNullOrEmpty(draft.ResponseText))
        {
            responses.Add(new DialogueResponse { Text = draft.ResponseText });
        }

        return new DialogueRecord
        {
            FormId = draft.FormId,
            EditorId = draft.EditorId,
            TopicFormId = draft.TopicFormId,
            InfoIndex = draft.InfoIndex,
            Responses = responses,
            SpeakerFormId = Resolve(draft.Speaker),
            SpeakerFactionFormId = Resolve(draft.Faction),
            SpeakerRaceFormId = Resolve(draft.Race)
        };
    }

    private static string? ReadString(byte[] data) =>
        data.Length == 0 ? null : EsmStringUtils.ReadNullTermString(data);
}
