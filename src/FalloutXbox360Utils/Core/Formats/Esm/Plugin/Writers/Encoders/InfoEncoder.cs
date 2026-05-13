using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="DialogueRecord" /> (INFO) as PC-format subrecord bytes.
///     Both override and new-record paths emit DATA + QSTI? + TPIC? + PNAM? + TRDT/NAM1 groups +
///     TCLT* + TCLF* + RNAM? + DNAM? + result-script blocks + ANAM? + NAME* + CTDA*.
///     INFO records typically have no EDID (the parent DIAL's EDID identifies the topic).
///
///     Override path emits the FULL canonical subrecord stream whenever the DMP captured
///     any meaningful content (responses / conditions / result scripts / prompt / etc.).
///     The merge engine does positional per-signature replacement and INFO has paired
///     subrecords (TRDT+NAM1 per response, SCHR+SCDA+SCTX+SCRO+NEXT per result-script
///     block), so partial emission would desynchronize the pairs — full-replace is the
///     only safe pattern. When the DMP has no content, override returns empty subrecords
///     and the engine retains all of the master's INFO verbatim.
///
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
        var info = (DialogueRecord)model;

        // Override emits only the subrecords whose positional per-signature merge is safe:
        // single-occurrence signatures (DATA / RNAM / DNAM / ANAM / QSTI / TPIC / PNAM) and
        // the paired TRDT+NAM1 response stream (response data + text pair up positionally,
        // so DMP[0..N] replaces master[0..N] while master[N..] remain intact).
        //
        // We deliberately SKIP CTDA, TCLT, TCLF, NAME, and result-script blocks because the
        // merge engine doesn't understand their semantic groupings — partial DMP captures
        // (e.g. 2 conditions when master has 5) would interleave with master's tail and
        // break AND/OR chains or script block boundaries. v22-initial-release emitted these
        // and showed up as random quest / dialogue failures in-game.
        if (!HasOverrideContent(info))
        {
            return new EncodedRecord { Subrecords = [], Warnings = [] };
        }

        var subs = new List<EncodedSubrecord>();
        BuildOverrideSafeSubrecords(info, subs);

        // If the override-safe pass produced nothing meaningful (only DATA was forced),
        // fall through to empty so the merge engine retains the master's INFO verbatim.
        if (subs.Count <= 1)
        {
            return new EncodedRecord { Subrecords = [], Warnings = [] };
        }

        return new EncodedRecord { Subrecords = subs, Warnings = [] };
    }

    /// <summary>
    ///     Emit only the override-safe subrecord subset: DATA + singletons (QSTI/TPIC/PNAM/
    ///     RNAM/DNAM/ANAM) + TRDT+NAM1 pairs. Skips multi-occurrence chains (CTDA, TCLT,
    ///     TCLF, NAME, result-script blocks) where partial positional replacement breaks
    ///     master logic.
    /// </summary>
    private static void BuildOverrideSafeSubrecords(DialogueRecord info, List<EncodedSubrecord> subs)
    {
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

        if (info.PreviousInfo.HasValue
            && info.PreviousInfo.Value != 0
            && info.PreviousInfo.Value != 0xFFFFFFFF
            && info.PreviousInfo.Value != info.FormId)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", info.PreviousInfo.Value));
        }

        // Response text pairs — positional merge keeps TRDT[i] aligned with NAM1[i].
        foreach (var response in info.Responses)
        {
            subs.Add(new EncodedSubrecord("TRDT", BuildTrdtSubrecord(response)));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1", response.Text ?? string.Empty));
        }

        if (!string.IsNullOrEmpty(info.PromptText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", info.PromptText));
        }

        if (info.Difficulty != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DNAM", info.Difficulty));
        }

        if (info.SpeakerFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ANAM", info.SpeakerFormId.Value));
        }
    }

    /// <summary>
    ///     Encode a new INFO record from scratch in fopdoc canonical order:
    ///     EDID?, DATA, QSTI?, TPIC?, PNAM?, (TRDT + NAM1)+, TCLT*, TCLF*, RNAM?, DNAM?,
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

        // New-record path needs a well-formed TRDT/NAM1 even if Responses is empty —
        // the runtime crashes iterating an empty response list on a fresh INFO record.
        BuildCanonicalSubrecords(info, subs, emitPlaceholderOnEmptyResponses: true);
        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Returns true when the DMP-parsed model carries any meaningful INFO content that
    ///     should reach the output ESP. Used by the override path to short-circuit empty
    ///     records so the merge engine retains the master verbatim.
    /// </summary>
    private static bool HasOverrideContent(DialogueRecord info)
    {
        return info.Responses.Count > 0
               || info.Conditions.Count > 0
               || info.ResultScripts.Count > 0
               || info.LinkToTopics.Count > 0
               || info.LinkFromTopics.Count > 0
               || info.AddTopics.Count > 0
               || !string.IsNullOrEmpty(info.PromptText)
               || info.Difficulty != 0
               || info.SpeakerFormId.HasValue;
    }

    /// <summary>
    ///     Emits the canonical INFO subrecord stream (everything except EDID). Shared
    ///     between the override path and the new-record path.
    /// </summary>
    private static void BuildCanonicalSubrecords(
        DialogueRecord info,
        List<EncodedSubrecord> subs,
        bool emitPlaceholderOnEmptyResponses)
    {
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

        // PNAM (previous info FormID): emit only when the value looks like a real reference.
        // 0x00000000 and 0xFFFFFFFF are sentinel "no previous" markers in runtime memory; emitting
        // those would trigger "Could not find previous info" at load time and break the dialog
        // chain walk. Self-references (PNAM == self) would also loop the runtime.
        if (info.PreviousInfo.HasValue
            && info.PreviousInfo.Value != 0
            && info.PreviousInfo.Value != 0xFFFFFFFF
            && info.PreviousInfo.Value != info.FormId)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", info.PreviousInfo.Value));
        }

        if (info.Responses.Count == 0)
        {
            // FNV runtime crashes when iterating an INFO's responses if the list is empty.
            // The new-record path emits an obvious placeholder so the structure is well-formed
            // and the user can see in GECK that the DMP didn't capture dialog text for this INFO.
            // The override path skips the placeholder — control wouldn't reach here unless
            // there's *other* content to override (HasOverrideContent gates it), and a missing
            // TRDT/NAM1 leaves the master's responses untouched via positional retain.
            if (emitPlaceholderOnEmptyResponses)
            {
                subs.Add(new EncodedSubrecord("TRDT", BuildPlaceholderTrdtSubrecord()));
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1",
                    "(NOT FOUND IN CRASH DUMP)"));
            }
        }
        else
        {
            foreach (var response in info.Responses)
            {
                subs.Add(new EncodedSubrecord("TRDT", BuildTrdtSubrecord(response)));
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1", response.Text ?? string.Empty));
            }
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

    /// <summary>
    ///     Placeholder TRDT for INFOs whose Responses list was empty in the parsed model
    ///     (typical for orphan INFOs carved from DMP memory where no NAM1 text survived).
    ///     Neutral emotion, response number 1, zeroed sound/conv-topic.
    /// </summary>
    private static byte[] BuildPlaceholderTrdtSubrecord()
    {
        var trdt = new byte[24];
        trdt[12] = 1; // ResponseNumber = 1 (first response)
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
