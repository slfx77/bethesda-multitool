using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;

/// <summary>
///     Builds the typed dialogue models (<see cref="DialogTopicRecord" /> / <see cref="DialogueRecord" />)
///     from Skyrim (TES5) DIAL and INFO subrecords, so the schema-driven reader can feed the shared,
///     game-agnostic <see cref="Handlers.DialogueTreeBuilder" /> and the Dialogue tab works for Skyrim.
///     <para>
///         Skyrim diverges from Oblivion/FNV in three ways that matter here:
///         (1) display text is <em>localized</em> — DIAL FULL and INFO RNAM are 4-byte .STRINGS ids and
///         INFO response text NAM1 is a 4-byte .ILSTRINGS id, all resolved through
///         <see cref="RecordParserContext" />; (2) the INFO carries an explicit <c>ANAM</c> speaker FormID
///         (→ NPC_), so speaker attribution is direct rather than inferred (CTDA GetIsID is only a
///         fallback); (3) response flags live in <c>ENAM</c>/<c>DATA</c> as a 16-bit field whose low byte
///         matches Oblivion's bit positions, and the CTDA condition is the 32-byte Skyrim layout (a union
///         comparison value and a trailing Run On / Reference / Parameter #3), though Type@0 / Function@8 /
///         Parameter #1@12 sit at the same offsets Oblivion uses. PC plugins are little-endian.
///     </para>
/// </summary>
internal sealed class SkyrimDialogueExtractor : IDialogueExtractor
{
    public static readonly SkyrimDialogueExtractor Instance = new();

    private SkyrimDialogueExtractor()
    {
    }

    // Skyrim condition function indices (wbDefinitionsTES5 — same low indices as FNV/Oblivion, plus
    // GetIsVoiceType for generic voiced dialogue).
    private const ushort GetIsRace = 69;
    private const ushort GetInFaction = 71;
    private const ushort GetIsId = 72;
    private const ushort GetIsVoiceType = 426;

    public DialogTopicRecord BuildTopic(
        uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs, RecordParserContext context)
    {
        string? fullName = null;
        byte category = 0;
        uint? questFormId = null;
        var priority = 0f;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "FULL":
                    fullName = context.ReadFullName(sub.Data);
                    break;
                case "QNAM" when sub.Data.Length >= 4:
                    questFormId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
                case "PNAM" when sub.Data.Length >= 4:
                    priority = BinaryPrimitives.ReadSingleLittleEndian(sub.Data);
                    break;
                // DATA: Do All Before Repeating (u8) + Category (u8) + Subtype (u16). Category is the
                // closest analogue to the model's topic-type byte; bytes 0-6 already render with sensible
                // names, but Skyrim's "Miscellaneous" (7) collides with the model's FNV-era "Radio" (7),
                // so fold it onto "Miscellaneous" (6).
                case "DATA" when sub.Data.Length >= 2:
                    category = sub.Data[1] == 7 ? (byte)6 : sub.Data[1];
                    break;
            }
        }

        return new DialogTopicRecord
        {
            FormId = formId,
            EditorId = editorId,
            FullName = fullName,
            TopicType = category,
            Priority = priority,
            QuestFormId = questFormId is > 0 ? questFormId : null
        };
    }

    public DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex,
        IReadOnlyList<RawSubrecord> subs, RecordParserContext context)
    {
        uint? questFormId = null;
        uint? previousInfo = null;
        byte infoFlags = 0;
        var responses = new List<DialogueResponse>();
        var linkToTopics = new List<uint>();
        var conditionFunctions = new List<ushort>();
        var conditions = new List<DialogueCondition>();
        uint? speakerFormId = null;
        uint? speakerFactionFormId = null;
        uint? speakerRaceFormId = null;
        uint? speakerVoiceTypeFormId = null;

        // Each response is a TRDT (emotion) followed by NAM1 (localized text); emit on NAM1 using the
        // last TRDT seen.
        var haveTrdt = false;
        uint emotionType = 0;
        var emotionValue = 0;
        byte responseNumber = 0;
        uint? soundFormId = null;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                // Response flags: ENAM stores them at byte 0, the older DATA at byte 2. The low byte's
                // bit positions match Oblivion's, so the shared remap applies.
                case "ENAM" when sub.Data.Length >= 1:
                    infoFlags = RemapInfoFlags(sub.Data[0]);
                    break;
                case "DATA" when sub.Data.Length >= 3 && infoFlags == 0:
                    infoFlags = RemapInfoFlags(sub.Data[2]);
                    break;
                case "PNAM" when sub.Data.Length >= 4:
                    previousInfo = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
                case "TCLT" when sub.Data.Length >= 4:
                    linkToTopics.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub.Data));
                    break;
                case "ANAM" when sub.Data.Length >= 4:
                    var anam = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    if (anam != 0)
                    {
                        speakerFormId ??= anam;
                    }

                    break;
                case "TRDT" when sub.Data.Length >= 16:
                    emotionType = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    emotionValue = BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(4));
                    responseNumber = sub.Data[12];
                    soundFormId = sub.Data.Length >= 20
                        ? BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(16))
                        : null;
                    haveTrdt = true;
                    break;
                case "NAM1":
                    responses.Add(new DialogueResponse
                    {
                        Text = context.ReadDialogueText(sub.Data),
                        EmotionType = haveTrdt ? emotionType : 0,
                        EmotionValue = haveTrdt ? emotionValue : 0,
                        ResponseNumber = haveTrdt ? responseNumber : (byte)0,
                        SoundFormId = haveTrdt && soundFormId is > 0 ? soundFormId : null
                    });
                    haveTrdt = false;
                    soundFormId = null;
                    break;
                case "CTDA" when sub.Data.Length >= 16:
                    ParseCondition(sub.Data, conditions, conditionFunctions,
                        ref speakerFormId, ref speakerFactionFormId, ref speakerRaceFormId,
                        ref speakerVoiceTypeFormId);
                    break;
            }
        }

        return new DialogueRecord
        {
            FormId = formId,
            EditorId = editorId,
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            PreviousInfo = previousInfo is > 0 ? previousInfo : null,
            InfoFlags = infoFlags,
            InfoIndex = infoIndex,
            Responses = responses,
            LinkToTopics = linkToTopics,
            ConditionFunctions = conditionFunctions,
            Conditions = conditions,
            SpeakerFormId = speakerFormId,
            SpeakerFactionFormId = speakerFactionFormId,
            SpeakerRaceFormId = speakerRaceFormId,
            SpeakerVoiceTypeFormId = speakerVoiceTypeFormId
        };
    }

    /// <summary>
    ///     Parses one Skyrim CTDA condition and, when it positively asserts the speaker's identity,
    ///     records the FormID. Skyrim CTDA layout (xEdit <c>wbCTDA</c>, little-endian, 32 bytes):
    ///     Type@0 (1) + unused (3) + Comparison Value@4 (float, or a GLOB FormID when Type bit 2 is set) +
    ///     Function@8 (u16) + unused (2) + Parameter #1@12 (4) + Parameter #2@16 (4) + Run On@20 (4) +
    ///     Reference@24 (4) + Parameter #3@28 (4). Only Type / Function / Parameter #1 are needed for
    ///     attribution, and they sit at the same offsets Oblivion uses. ANAM remains the primary speaker
    ///     source; this fills generic/voiced lines that have no ANAM.
    /// </summary>
    private static void ParseCondition(
        byte[] data,
        List<DialogueCondition> conditions,
        List<ushort> conditionFunctions,
        ref uint? speaker,
        ref uint? faction,
        ref uint? race,
        ref uint? voiceType)
    {
        var typeByte = data[0];
        var usesGlobal = (typeByte & 0x04) != 0;
        var comparisonValue = usesGlobal ? 0f : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4));
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

        // "Speaker is X" reads as the function equalling true (== / >= ~1) or not-false (!= / > ~0). A
        // global comparison value can't be evaluated statically, so treat it as non-asserting.
        var compOp = (typeByte >> 5) & 0x7;
        var isPositive = !usesGlobal &&
                         ((compOp is 0 or 3 && comparisonValue >= 0.99f) ||
                          (compOp is 1 or 2 && comparisonValue < 0.01f));
        if (!isPositive || param1 == 0)
        {
            return;
        }

        switch (functionIndex)
        {
            case GetIsId:
                speaker ??= param1;
                break;
            case GetInFaction:
                faction ??= param1;
                break;
            case GetIsRace:
                race ??= param1;
                break;
            case GetIsVoiceType:
                voiceType ??= param1;
                break;
        }
    }

    /// <summary>
    ///     Remaps the low byte of Skyrim's 16-bit INFO response-flags field onto the
    ///     <see cref="DialogueRecord" /> model's FNV bit layout so its computed properties
    ///     (IsGoodbye/IsSayOnce/...) read correctly. Skyrim bits: Goodbye(0x01) Random(0x02) SayOnce(0x04)
    ///     RequiresPlayerActivation(0x08) InfoRefusal(0x10) RandomEnd(0x20) InvisibleContinue(0x40)
    ///     WalkAway(0x80).
    /// </summary>
    private static byte RemapInfoFlags(byte skyrim)
    {
        byte flags = 0;
        if ((skyrim & 0x01) != 0) flags |= 0x01; // Goodbye
        if ((skyrim & 0x02) != 0) flags |= 0x02; // Random
        if ((skyrim & 0x04) != 0) flags |= DialogueRecord.SayOnceFlag;
        if ((skyrim & 0x20) != 0) flags |= DialogueRecord.RandomEndFlag;
        return flags;
    }
}
