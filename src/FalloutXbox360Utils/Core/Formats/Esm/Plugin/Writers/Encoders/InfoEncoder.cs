using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="DialogueRecord" /> (INFO) as PC-format subrecord bytes.
///     v6 emits the full record from scratch: DATA + QSTI? + TPIC? + PNAM? + TRDT/NAM1 groups +
///     TCLT* + TCLF* + RNAM? + DNAM? + result-script blocks + ANAM? + NAME* + CTDA*.
///     INFO records typically have no EDID (the parent DIAL's EDID identifies the topic).
///     DATA (4 bytes): DialType(0) + NextSpeaker(1) + Flags(2) + Flags2(3) — raw bytes.
///     TRDT (24 bytes) per PDB RESPONSE_DATA:
///         uint32 EmotionType(0) + int32 EmotionValue(4) + FormID ConvTopic(8) +
///         uint8 ResponseID(12) + pad(13..15) + FormID Sound(16) + bool UseEmotion(20) + pad(21..23).
///     CTDA (28 bytes) per PDB CONDITION_ITEM_DATA:
///         uint8 Type(0) + pad(1..3) + float ComparisonValue(4) + uint16 FunctionIndex(8) +
///         pad(10..11) + FormID Parameter1(12) + uint32 Parameter2(16) + uint32 RunOn(20) +
///         FormID Reference(24).
/// </summary>
public sealed class InfoEncoder : IRecordEncoder
{
    public string RecordType => "INFO";
    public Type ModelType => typeof(DialogueRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new INFO record from scratch in fopdoc canonical order:
    ///     DATA, QSTI?, TPIC?, PNAM?, (TRDT + NAM1)+, TCLT*, TCLF*, RNAM?, DNAM?,
    ///     (SCHR + SCDA? + SCTX? + SCRO* + SCRV* + NEXT?)*, ANAM?, NAME*, CTDA*.
    /// </summary>
    internal static EncodedRecord EncodeNew(DialogueRecord info)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        // Most INFO records have no EDID — they're identified by FormID under their parent DIAL.
        // Emit only when the model carries one (rare).
        if (!string.IsNullOrEmpty(info.EditorId))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", info.EditorId));
        }

        // DATA: DialType + NextSpeaker default 0; model carries Flags/Flags2.
        var data = new byte[4];
        data[2] = info.InfoFlags;
        data[3] = info.InfoFlagsExt;
        subs.Add(new EncodedSubrecord("DATA", data));

        if (info.QuestFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QSTI", info.QuestFormId.Value));
        }

        if (info.TopicFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TPIC", info.TopicFormId.Value));
        }

        if (info.PreviousInfo.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", info.PreviousInfo.Value));
        }

        foreach (var response in info.Responses)
        {
            subs.Add(new EncodedSubrecord("TRDT", BuildTrdtSubrecord(response)));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1", response.Text ?? string.Empty));
        }

        foreach (var formId in info.LinkToTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TCLT", formId));
        }

        foreach (var formId in info.LinkFromTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TCLF", formId));
        }

        if (!string.IsNullOrEmpty(info.PromptText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", info.PromptText));
        }

        if (info.Difficulty != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DNAM", info.Difficulty));
        }

        // Result-script blocks (multiple, separated by NEXT). Each block has its own SCHR,
        // optional SCDA + SCTX, and zero or more SCRO/SCRV referenced-object entries.
        for (var i = 0; i < info.ResultScripts.Count; i++)
        {
            EmitResultScriptBlock(subs, info.ResultScripts[i], isLast: i == info.ResultScripts.Count - 1);
        }

        if (info.SpeakerFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ANAM", info.SpeakerFormId.Value));
        }

        foreach (var formId in info.AddTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAME", formId));
        }

        foreach (var condition in info.Conditions)
        {
            subs.Add(new EncodedSubrecord("CTDA", BuildCtdaSubrecord(condition)));
            if (!string.IsNullOrEmpty(condition.Parameter1String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS1", condition.Parameter1String));
            }

            if (!string.IsNullOrEmpty(condition.Parameter2String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS2", condition.Parameter2String));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static void EmitResultScriptBlock(
        List<EncodedSubrecord> subs,
        DialogueResultScript script,
        bool isLast)
    {
        var compiledSize = script.CompiledData?.Length ?? 0;
        var refCount = (uint)script.ReferencedObjects.Count;

        // Inline result scripts are Object-type (not quest, not magic-effect). IsCompiled
        // mirrors whether the model carries bytecode — if we have SCDA bytes we'll emit them.
        var schr = new byte[20];
        SubrecordEncoder.WriteUInt32(schr, 0, 0); // VariableCount — INFO result scripts have no locals
        SubrecordEncoder.WriteUInt32(schr, 4, refCount);
        SubrecordEncoder.WriteUInt32(schr, 8, (uint)compiledSize);
        SubrecordEncoder.WriteUInt32(schr, 12, 0); // LastVariableId
        schr[16] = 0; // IsQuestScript
        schr[17] = 0; // IsMagicEffectScript
        schr[18] = compiledSize > 0 ? (byte)1 : (byte)0; // IsCompiled
        subs.Add(new EncodedSubrecord("SCHR", schr));

        if (script.CompiledData is { Length: > 0 } compiled)
        {
            subs.Add(new EncodedSubrecord("SCDA", compiled));
        }

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCTX", script.SourceText));
        }

        foreach (var refFormId in script.ReferencedObjects)
        {
            if ((refFormId & 0x80000000) != 0)
            {
                var varIndex = refFormId & 0x7FFFFFFF;
                subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("SCRV", varIndex));
            }
            else
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", refFormId));
            }
        }

        // NEXT separator delimits multiple result-script blocks. The final block typically
        // doesn't carry NEXT — the parser flushes on its own at end-of-record. Honor the
        // model's flag when present and avoid forcing a trailing separator.
        if (script.HasNextSeparator && !isLast)
        {
            subs.Add(new EncodedSubrecord("NEXT", []));
        }
    }

    private static byte[] BuildTrdtSubrecord(Models.Dialogue.DialogueResponse response)
    {
        var trdt = new byte[24];
        SubrecordEncoder.WriteUInt32(trdt, 0, response.EmotionType);
        SubrecordEncoder.WriteInt32(trdt, 4, response.EmotionValue);
        // bytes 8-11: ConversationTopic FormID (not in model, zero)
        trdt[12] = response.ResponseNumber;
        // bytes 13-15: padding
        // bytes 16-19: Sound FormID (not in model, zero)
        // byte 20: UseEmotionAnim bool (not in model, zero)
        // bytes 21-23: padding
        return trdt;
    }

    internal static byte[] BuildCtdaSubrecord(DialogueCondition condition)
    {
        var ctda = new byte[28];
        ctda[0] = condition.Type;
        // bytes 1-3: padding
        SubrecordEncoder.WriteFloat(ctda, 4, condition.ComparisonValue);
        SubrecordEncoder.WriteUInt16(ctda, 8, condition.FunctionIndex);
        // bytes 10-11: padding
        SubrecordEncoder.WriteFormId(ctda, 12, condition.Parameter1);
        SubrecordEncoder.WriteUInt32(ctda, 16, condition.Parameter2);
        SubrecordEncoder.WriteUInt32(ctda, 20, condition.RunOn);
        SubrecordEncoder.WriteFormId(ctda, 24, condition.Reference);
        return ctda;
    }
}
