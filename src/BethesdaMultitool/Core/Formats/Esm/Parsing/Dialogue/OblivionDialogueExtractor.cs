using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;

/// <summary>
///     Builds the typed dialogue models (<see cref="DialogTopicRecord" /> / <see cref="DialogueRecord" />)
///     from Oblivion (TES4) DIAL and INFO subrecords, so the schema-driven reader can feed the shared,
///     game-agnostic <see cref="Handlers.DialogueTreeBuilder" /> and the Dialogue tab works for Oblivion.
///     Oblivion's INFO layout differs from FNV's: response text is NAM1, emotion is TRDT, and the DATA
///     flag bits sit at different positions than the <see cref="DialogueRecord" /> model's FNV layout
///     (remapped in <see cref="RemapInfoFlags" />). PC plugins are little-endian.
/// </summary>
internal static class OblivionDialogueExtractor
{
    public static DialogTopicRecord BuildTopic(uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs)
    {
        string? fullName = null;
        byte topicType = 0;
        uint? questFormId = null;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "FULL":
                    fullName = ReadString(sub.Data);
                    break;
                case "DATA" when sub.Data.Length >= 1:
                    topicType = sub.Data[0];
                    break;
                case "QSTI" when sub.Data.Length >= 4 && questFormId is null:
                    questFormId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
            }
        }

        return new DialogTopicRecord
        {
            FormId = formId,
            EditorId = editorId,
            FullName = fullName,
            TopicType = topicType,
            QuestFormId = questFormId is > 0 ? questFormId : null
        };
    }

    public static DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex, IReadOnlyList<RawSubrecord> subs)
    {
        uint? questFormId = null;
        uint? previousInfo = null;
        byte infoFlags = 0;
        var responses = new List<DialogueResponse>();
        var addTopics = new List<uint>();
        var linkToTopics = new List<uint>();

        // Each response is a TRDT (emotion) followed by NAM1 (text); emit on NAM1 using the last TRDT.
        var haveTrdt = false;
        uint emotionType = 0;
        var emotionValue = 0;
        byte responseNumber = 0;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "DATA" when sub.Data.Length >= 3:
                    infoFlags = RemapInfoFlags(sub.Data[2]);
                    break;
                case "QSTI" when sub.Data.Length >= 4:
                    questFormId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
                case "PNAM" when sub.Data.Length >= 4:
                    previousInfo = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
                case "NAME" when sub.Data.Length >= 4:
                    addTopics.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub.Data));
                    break;
                case "TCLT" when sub.Data.Length >= 4:
                    linkToTopics.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub.Data));
                    break;
                case "TRDT" when sub.Data.Length >= 16:
                    emotionType = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    emotionValue = BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(4));
                    responseNumber = sub.Data[12];
                    haveTrdt = true;
                    break;
                case "NAM1":
                    responses.Add(new DialogueResponse
                    {
                        Text = ReadString(sub.Data),
                        EmotionType = haveTrdt ? emotionType : 0,
                        EmotionValue = haveTrdt ? emotionValue : 0,
                        ResponseNumber = haveTrdt ? responseNumber : (byte)0
                    });
                    haveTrdt = false;
                    break;
            }
        }

        return new DialogueRecord
        {
            FormId = formId,
            EditorId = editorId,
            TopicFormId = topicFormId,
            QuestFormId = questFormId is > 0 ? questFormId : null,
            PreviousInfo = previousInfo is > 0 ? previousInfo : null,
            InfoFlags = infoFlags,
            InfoIndex = infoIndex,
            Responses = responses,
            AddTopics = addTopics,
            LinkToTopics = linkToTopics
        };
    }

    /// <summary>
    ///     Remaps Oblivion INFO DATA flag bits onto the <see cref="DialogueRecord" /> model's FNV bit
    ///     layout so its computed properties (IsGoodbye/IsSayOnce/...) read correctly. Oblivion bits:
    ///     Goodbye(0x01) Random(0x02) SayOnce(0x04) RunImmediately(0x08) InfoRefusal(0x10) RandomEnd(0x20)
    ///     RunForRumors(0x40).
    /// </summary>
    private static byte RemapInfoFlags(byte oblivion)
    {
        byte flags = 0;
        if ((oblivion & 0x01) != 0) flags |= 0x01; // Goodbye
        if ((oblivion & 0x02) != 0) flags |= 0x02; // Random
        if ((oblivion & 0x04) != 0) flags |= 0x10; // Say Once
        if ((oblivion & 0x20) != 0) flags |= 0x04; // Random End
        if ((oblivion & 0x40) != 0) flags |= 0x08; // Run for Rumors
        return flags;
    }

    private static string? ReadString(byte[] data) =>
        data.Length == 0 ? null : EsmStringUtils.ReadNullTermString(data);
}
