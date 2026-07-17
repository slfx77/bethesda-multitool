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
internal sealed class OblivionDialogueExtractor : IDialogueExtractor
{
    public static readonly OblivionDialogueExtractor Instance = new();

    private OblivionDialogueExtractor()
    {
    }

    /// <remarks>Oblivion dialogue text is inline (no localized-string tables), so the context is unused.</remarks>
    public DialogTopicRecord BuildTopic(
        uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs, RecordParserContext context)
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

    /// <inheritdoc cref="BuildTopic" />
    public DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex,
        IReadOnlyList<RawSubrecord> subs, RecordParserContext context)
    {
        uint? questFormId = null;
        uint? previousInfo = null;
        byte infoFlags = 0;
        var responses = new List<DialogueResponse>();
        var addTopics = new List<uint>();
        var linkToTopics = new List<uint>();
        var conditionFunctions = new List<ushort>();
        var conditions = new List<DialogueCondition>();
        uint? speakerFormId = null;
        uint? speakerFactionFormId = null;
        uint? speakerRaceFormId = null;

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
                case "CTDA" when sub.Data.Length >= 16:
                    ParseCondition(sub.Data, conditions, conditionFunctions,
                        ref speakerFormId, ref speakerFactionFormId, ref speakerRaceFormId);
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
            LinkToTopics = linkToTopics,
            ConditionFunctions = conditionFunctions,
            Conditions = conditions,
            SpeakerFormId = speakerFormId,
            SpeakerFactionFormId = speakerFactionFormId,
            SpeakerRaceFormId = speakerRaceFormId
        };
    }

    /// <summary>
    ///     Parses one Oblivion CTDA condition and, when it positively asserts the speaker's identity,
    ///     records the FormID. Oblivion CTDA layout (xEdit <c>wbConditionMembers</c>, little-endian):
    ///     Type@0 (1) + unused (3) + Comparison Value@4 (float) + Function@8 (u16) + unused (2) +
    ///     Parameter #1@12 (4) + Parameter #2@16 (4). There is no RunOn field (FNV-only), so a positive
    ///     condition asserts the dialogue subject directly. Function indices match FNV's:
    ///     GetIsRace=0x45, GetInFaction=0x47, GetIsID=0x48 (Oblivion has no GetIsVoiceType).
    /// </summary>
    private static void ParseCondition(
        byte[] data,
        List<DialogueCondition> conditions,
        List<ushort> conditionFunctions,
        ref uint? speaker,
        ref uint? faction,
        ref uint? race)
    {
        var typeByte = data[0];
        var comparisonValue = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4));
        var functionIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var param1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var param2 = data.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) : 0u;

        conditionFunctions.Add(functionIndex);
        conditions.Add(new DialogueCondition
        {
            Type = typeByte,
            ComparisonValue = comparisonValue,
            FunctionIndex = functionIndex,
            Parameter1 = param1,
            Parameter2 = param2
        });

        // "Speaker is X" reads as the function equalling true (== / >= ~1) or not-false (!= / > ~0).
        var compOp = (typeByte >> 5) & 0x7;
        var isPositive = (compOp is 0 or 3 && comparisonValue >= 0.99f) ||
                         (compOp is 1 or 2 && comparisonValue < 0.01f);
        if (!isPositive || param1 == 0)
        {
            return;
        }

        switch (functionIndex)
        {
            case 0x48: // GetIsID
                speaker ??= param1;
                break;
            case 0x47: // GetInFaction
                faction ??= param1;
                break;
            case 0x45: // GetIsRace
                race ??= param1;
                break;
        }
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
        if ((oblivion & 0x04) != 0) flags |= DialogueRecord.SayOnceFlag;
        if ((oblivion & 0x08) != 0) flags |= DialogueRecord.RunImmediatelyFlag;
        if ((oblivion & 0x10) != 0) flags |= DialogueRecord.InfoRefusalFlag;
        if ((oblivion & 0x20) != 0) flags |= DialogueRecord.RandomEndFlag;
        if ((oblivion & 0x40) != 0) flags |= DialogueRecord.RunForRumorsFlag;
        return flags;
    }

    private static string? ReadString(byte[] data) =>
        data.Length == 0 ? null : EsmStringUtils.ReadNullTermString(data);
}
