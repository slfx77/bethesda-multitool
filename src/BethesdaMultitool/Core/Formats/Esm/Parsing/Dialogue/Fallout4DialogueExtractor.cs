using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;

/// <summary>
///     Builds the typed dialogue models (<see cref="DialogTopicRecord" /> / <see cref="DialogueRecord" />)
///     from Fallout 4 (FO4) DIAL and INFO subrecords, so the schema-driven reader can feed the shared,
///     game-agnostic <see cref="Handlers.DialogueTreeBuilder" /> and the Dialogue tab works for FO4.
///     <b>Fallout 76 reuses this extractor</b> — its DIAL/INFO layout (TRDA response struct, ENAM flags,
///     localized NAM1/RNAM, ANAM speaker, 32-byte CTDA) is identical to FO4's; only the DIAL DATA Category
///     enum differs, which affects the cosmetic topic-type label alone.
///     <para>
///         FO4 sits on the same Skyrim-era framing (localized text, explicit ANAM speaker, 32-byte CTDA,
///         DIAL FULL/QNAM/DATA-Category), so the topic build and condition parse mirror
///         <see cref="SkyrimDialogueExtractor" />. The INFO response struct differs: FO4 uses <c>TRDA</c>
///         (emotion is a Keyword FormID, not the old numeric enum; response number at byte 4) in place of
///         Skyrim's <c>TRDT</c>, the response flags sit in <c>ENAM</c> with FO4 bit positions (no Goodbye
///         bit), and FO4 dropped the TCLT/NAME topic-linking subrecords (flow is scene/quest driven).
///         PC plugins are little-endian.
///     </para>
/// </summary>
internal static class Fallout4DialogueExtractor
{
    // FO4 condition function indices (wbDefinitionsFO4 — same as Skyrim/FNV).
    private const ushort GetIsRace = 69;
    private const ushort GetInFaction = 71;
    private const ushort GetIsId = 72;
    private const ushort GetIsVoiceType = 426;

    public static DialogTopicRecord BuildTopic(
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
                // DATA: Topic Flags (u8) + Category (u8) + Subtype (u16). Category is the closest analogue
                // to the model's topic-type byte; bytes 0-6 render with sensible names, but FO4's
                // "Miscellaneous" (7) collides with the model's FNV-era "Radio" (7), so fold it onto 6.
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

    public static DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex,
        IReadOnlyList<RawSubrecord> subs, RecordParserContext context)
    {
        uint? previousInfo = null;
        byte infoFlags = 0;
        var responses = new List<DialogueResponse>();
        var conditionFunctions = new List<ushort>();
        var conditions = new List<DialogueCondition>();
        uint? speakerFormId = null;
        uint? speakerFactionFormId = null;
        uint? speakerRaceFormId = null;
        uint? speakerVoiceTypeFormId = null;

        // Each response is a TRDA (metadata) followed by NAM1 (localized text); emit on NAM1 using the
        // last TRDA seen.
        var haveTrda = false;
        byte responseNumber = 0;
        uint? soundFormId = null;

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                // ENAM: response flags (u16) + reset hours (u16). FO4 bit positions differ from
                // Oblivion/Skyrim (bit 0 is "Start Scene on End", not Goodbye), so remap explicitly.
                case "ENAM" when sub.Data.Length >= 1:
                    infoFlags = RemapInfoFlags(sub.Data[0]);
                    break;
                case "PNAM" when sub.Data.Length >= 4:
                    previousInfo = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    break;
                case "ANAM" when sub.Data.Length >= 4:
                    var anam = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                    if (anam != 0)
                    {
                        speakerFormId ??= anam;
                    }

                    break;
                // TRDA: Emotion (Keyword FormID) @0, Response number @4, Sound File (FormID) @5, ...
                // The emotion is a keyword rather than the old numeric enum, so leave EmotionType neutral.
                case "TRDA" when sub.Data.Length >= 5:
                    responseNumber = sub.Data[4];
                    soundFormId = sub.Data.Length >= 9
                        ? BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(5))
                        : null;
                    haveTrda = true;
                    break;
                case "NAM1":
                    responses.Add(new DialogueResponse
                    {
                        Text = context.ReadDialogueText(sub.Data),
                        ResponseNumber = haveTrda ? responseNumber : (byte)0,
                        SoundFormId = haveTrda && soundFormId is > 0 ? soundFormId : null
                    });
                    haveTrda = false;
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
            PreviousInfo = previousInfo is > 0 ? previousInfo : null,
            InfoFlags = infoFlags,
            InfoIndex = infoIndex,
            Responses = responses,
            ConditionFunctions = conditionFunctions,
            Conditions = conditions,
            SpeakerFormId = speakerFormId,
            SpeakerFactionFormId = speakerFactionFormId,
            SpeakerRaceFormId = speakerRaceFormId,
            SpeakerVoiceTypeFormId = speakerVoiceTypeFormId
        };
    }

    /// <summary>
    ///     Parses one FO4 CTDA condition and, when it positively asserts the speaker's identity, records
    ///     the FormID. The layout matches Skyrim's 32-byte CTDA: Type@0 / Function@8 / Parameter #1@12 are
    ///     all that attribution needs. ANAM remains the primary speaker source; this fills generic/voiced
    ///     lines that have no ANAM.
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
    ///     Remaps the low byte of FO4's 16-bit INFO response-flags field onto the
    ///     <see cref="DialogueRecord" /> model's FNV bit layout. FO4 bits: Start Scene on End(0x01)
    ///     Random(0x02) SayOnce(0x04) RequiresPlayerActivation(0x08) Unknown(0x10) RandomEnd(0x20) — there
    ///     is no plain Goodbye bit (flow is scene driven), so only Random/SayOnce/RandomEnd carry over.
    /// </summary>
    private static byte RemapInfoFlags(byte fo4)
    {
        byte flags = 0;
        if ((fo4 & 0x02) != 0) flags |= 0x02; // Random
        if ((fo4 & 0x04) != 0) flags |= DialogueRecord.SayOnceFlag;
        if ((fo4 & 0x20) != 0) flags |= DialogueRecord.RandomEndFlag;
        return flags;
    }
}
