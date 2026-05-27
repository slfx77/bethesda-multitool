using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Script;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="DialogueRecord" /> (INFO) as PC-format subrecord bytes.
///     Both override and new-record paths emit DATA + QSTI? + TPIC? + PNAM? + TRDT/NAM1 groups +
///     TCLT* + TCLF* + TCFU* + RNAM? + DNAM? + result-script blocks + ANAM? + NAME* + CTDA*.
///     INFO records typically have no EDID (the parent DIAL's EDID identifies the topic).
///     Override path emits the FULL canonical subrecord stream whenever the DMP captured
///     any meaningful content (responses / conditions / result scripts / prompt / etc.).
///     The merge engine does positional per-signature replacement and INFO has paired
///     subrecords (TRDT+NAM1 per response, SCHR+SCDA+SCTX+SCRO+NEXT per result-script
///     block), so partial emission would desynchronize the pairs — full-replace is the
///     only safe pattern. When the DMP has no content, override returns empty subrecords
///     and the engine retains all of the master's INFO verbatim.
///     DATA (4 bytes): DialType(0) + NextSpeaker(1) + Flags(2) + Flags2(3) — raw bytes.
///     TRDT (24 bytes) per PDB RESPONSE_DATA:
///     uint32 EmotionType(0) + int32 EmotionValue(4) + FormID ConvTopic(8) +
///     uint8 ResponseID(12) + pad(13..15) + FormID Sound(16) + bool UseEmotion(20) + pad(21..23).
///     CTDA (28 bytes) per PDB CONDITION_ITEM_DATA:
///     uint8 Type(0) + pad(1..3) + float ComparisonValue(4) + uint16 FunctionIndex(8) +
///     pad(10..11) + FormID Parameter1(12) + uint32 Parameter2(16) + uint32 RunOn(20) +
///     FormID Reference(24).
/// </summary>
public sealed class InfoEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<DialogueResponse, object?>> TrdtExtractors = new(StringComparer.Ordinal)
    {
        ["EmotionType"] = m => m.EmotionType,
        ["EmotionValue"] = m => m.EmotionValue,
        // ConversationTopic + UseEmotionAnim not in model → zero-fill.
        ["ResponseNumber"] = m => m.ResponseNumber,
        ["Sound"] = m => m.SoundFormId ?? 0u,
    };

    private static readonly Dictionary<string, Func<DialogueCondition, object?>> CtdaExtractors = new(StringComparer.Ordinal)
    {
        ["Type"] = m => m.Type,
        ["ComparisonValue"] = m => m.ComparisonValue,
        ["FunctionIndex"] = m => m.FunctionIndex,
        ["Parameter1"] = m => m.Parameter1,
        ["Parameter2"] = m => m.Parameter2,
        ["RunOn"] = m => m.RunOn,
        ["Reference"] = m => m.Reference,
    };

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
        // break AND/OR chains or script block boundaries. Emitting these caused random
        // quest / dialogue failures in-game.
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

        // Response groups — vanilla INFOs emit (TRDT, NAM1, NAM2, NAM3) per response. NAM2
        // (Actor Notes, GECK editorial only) and NAM3 (Edits, GECK editorial only) are
        // single-null-byte empty strings in shipping content, but the FNV engine uses them
        // as the response-record boundary marker when walking the response list — without
        // them the next response's TRDT is misread as overflow on the prior response and
        // the engine SILENTLY skips voice playback for the whole INFO (no error log, no
        // audio). Confirmed against every vanilla voiced INFO (e.g. 0x001611DE, 0x00167749):
        // every response is exactly TRDT[24] + NAM1[N] + NAM2[1]=0x00 + NAM3[1]=0x00.
        foreach (var response in info.Responses)
        {
            subs.Add(new EncodedSubrecord("TRDT", BuildTrdtSubrecord(response)));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1", response.Text ?? string.Empty));
            // NAM2 = empty actor notes (1-byte null terminator).
            subs.Add(new EncodedSubrecord("NAM2", [0]));
            // NAM3 = empty edits (1-byte null terminator).
            subs.Add(new EncodedSubrecord("NAM3", [0]));
        }

        if (!string.IsNullOrEmpty(info.PromptText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", info.PromptText));
        }

        if (info.SpeakerFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ANAM", info.SpeakerFormId.Value));
        }

        if (info.Difficulty != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DNAM", info.Difficulty));
        }
    }

    /// <summary>
    ///     Encode a new INFO record from scratch in xEdit canonical FNV order:
    ///     EDID?, DATA, QSTI?, TPIC?, PNAM?, NAME*, (TRDT + NAM1)+,
    ///     (CTDA + CIS1? + CIS2?)*, TCLT*, TCLF*, TCFU*,
    ///     (SCHR + SCDA? + SCTX? + SCRO* + SCRV* + NEXT?)*, RNAM?, ANAM?, DNAM?.
    /// </summary>
    /// <param name="info">INFO model to emit.</param>
    /// <param name="validFormIds">
    ///     Master ∪ newly-emitted FormID set for validating embedded result-script SCRO
    ///     references. See <see cref="ScptEncoder.EncodeNew" />.
    /// </param>
    /// <param name="remapTable">
    ///     Source→allocated FormID alias map for embedded result-script SCROs.
    /// </param>
    internal static EncodedRecord EncodeNew(
        DialogueRecord info,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
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
        BuildCanonicalSubrecords(info, subs, true, validFormIds, remapTable);
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
               || info.FollowUpInfos.Count > 0
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
        bool emitPlaceholderOnEmptyResponses,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
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

        // NAME (Add Topics) appears EARLY in xEdit's FNV INFO definition — before responses,
        // not at the end. Moving it late produces FNVEdit "unexpected NAME" errors and the
        // engine's parser misclassifies later subrecords, corrupting form bindings.
        foreach (var formId in info.AddTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAME", formId));
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
                // Placeholder still needs the per-response (NAM2, NAM3) boundary markers —
                // see the comment above the new-INFO loop for the engine-side rationale.
                subs.Add(new EncodedSubrecord("NAM2", [0]));
                subs.Add(new EncodedSubrecord("NAM3", [0]));
            }
        }
        else
        {
            // Vanilla INFOs emit (TRDT, NAM1, NAM2[1]=0x00, NAM3[1]=0x00) per response.
            // NAM2/NAM3 are GECK editorial fields (Actor Notes / Edits), empty in shipping
            // content, but the FNV runtime uses them as the response-record boundary marker
            // when walking the response list — without them the next response's TRDT is
            // misread as overflow on the prior response and the engine SILENTLY skips voice
            // playback for the whole INFO (no error log, no audio). Confirmed against every
            // vanilla voiced INFO (e.g. 0x001611DE, 0x00167749): every response is exactly
            // TRDT[24] + NAM1[N] + NAM2[1]=0x00 + NAM3[1]=0x00.
            foreach (var response in info.Responses)
            {
                subs.Add(new EncodedSubrecord("TRDT", BuildTrdtSubrecord(response)));
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NAM1", response.Text ?? string.Empty));
                subs.Add(new EncodedSubrecord("NAM2", [0]));
                subs.Add(new EncodedSubrecord("NAM3", [0]));
            }
        }

        // CTDA (with CIS1/CIS2) goes AFTER responses, BEFORE TCLT/TCLF per xEdit canonical.
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

        foreach (var formId in info.LinkToTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TCLT", formId));
        }

        foreach (var formId in info.LinkFromTopics)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TCLF", formId));
        }

        // TCFU links are the serialized follow-up INFO list. Runtime captures expose
        // these as TESConversationData.m_listFollowUpInfos; topic-menu links themselves
        // are TCLT and are synthesized by DialogGrupBuilder when a terminal response
        // needs to return to its GREETING root topic list.
        foreach (var formId in info.FollowUpInfos)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TCFU", formId));
        }

        // Result-script blocks: master ALWAYS emits Begin Script + NEXT + End Script in INFO
        // records, even when both are empty (just bare SCHR headers). Skipping them produces
        // FNVEdit "unexpected (or out of order) subrecord SCHR" errors when other subrecords
        // come later in the canonical order, and the engine's parser can lose its alignment
        // mid-record. Always emit the pair; pad missing blocks with empty SCHR headers.
        var beginScript = info.ResultScripts.Count > 0 ? info.ResultScripts[0] : null;
        var endScript = info.ResultScripts.Count > 1 ? info.ResultScripts[1] : null;
        EmitResultScriptBlock(subs, beginScript, validFormIds, remapTable);
        subs.Add(new EncodedSubrecord("NEXT", []));
        EmitResultScriptBlock(subs, endScript, validFormIds, remapTable);

        if (!string.IsNullOrEmpty(info.PromptText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", info.PromptText));
        }

        if (info.SpeakerFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ANAM", info.SpeakerFormId.Value));
        }

        if (info.Difficulty != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DNAM", info.Difficulty));
        }
    }

    internal static void EmitResultScriptBlock(
        List<EncodedSubrecord> subs,
        DialogueResultScript? script,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var compiledSize = script?.CompiledData?.Length ?? 0;
        var refCount = (uint)(script?.ReferencedObjects.Count ?? 0);

        // Inline result scripts are Object-type (not quest, not magic-effect). Master ESM
        // always sets IsCompiled=1 even when CompiledSize=0 — the engine reads this as
        // "compiled form, no bytecode", a no-op. Setting IsCompiled=0 on an empty script
        // tells the engine the script needs runtime compilation, which can trigger spurious
        // behavior (observed: actors playing crucified idle every few seconds).
        // Use SchemaDictionarySerializer directly because the values are computed from the
        // result script's referenced-objects and compiled-data sizes, not model properties.
        var schrSchema = SubrecordSchemaRegistry.GetSchema("SCHR", "", 20)
            ?? throw new InvalidOperationException("SCHR schema missing.");
        var schrValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["VariableCount"] = 0u,
            ["RefObjectCount"] = refCount,
            ["CompiledSize"] = (uint)compiledSize,
            ["LastVariableId"] = 0u,
            ["IsQuestScript"] = (byte)0,
            ["IsMagicEffectScript"] = (byte)0,
            ["IsCompiled"] = (byte)1,
        };
        subs.Add(new EncodedSubrecord("SCHR", SchemaDictionarySerializer.Serialize(schrSchema, schrValues)));

        if (script is null)
        {
            return;
        }

        if (script.CompiledData is { Length: > 0 } compiled)
        {
            // BE bytecode from DMP-sourced INFO result scripts must be swapped to LE for the
            // PC engine — same reason as SCPT. See ScptEncoder.cs for the long explanation.
            var scda = script.IsBigEndianBytecode
                ? ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
                    compiled, variables: null, script.ReferencedObjects)
                : compiled;
            subs.Add(new EncodedSubrecord("SCDA", scda));
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
                // SCROs in INFO result scripts go through the same alias/validity check as
                // top-level SCPT records — see ScptEncoder.EncodeNew for rationale. Without
                // this, the engine refuses to execute result scripts that reference any
                // remapped or proto-only FormID, breaking dialogue side-effects.
                var resolved = FormIdReferenceResolver.Resolve(refFormId, validFormIds, remapTable) ?? 0u;
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", resolved));
            }
        }
    }

    private static byte[] BuildTrdtSubrecord(DialogueResponse response)
    {
        return SchemaModelSerializer.Serialize("TRDT", "", 24, response, TrdtExtractors);
    }

    /// <summary>
    ///     Placeholder TRDT for INFOs whose Responses list was empty in the parsed model
    ///     (typical for orphan INFOs carved from DMP memory where no NAM1 text survived).
    ///     Neutral emotion, response number 1, zeroed sound/conv-topic.
    /// </summary>
    private static byte[] BuildPlaceholderTrdtSubrecord()
    {
        return SchemaModelSerializer.Serialize("TRDT", "", 24,
            new DialogueResponse { ResponseNumber = 1 }, TrdtExtractors);
    }

    internal static byte[] BuildCtdaSubrecord(DialogueCondition condition)
    {
        return SchemaModelSerializer.Serialize("CTDA", "", 28, condition, CtdaExtractors);
    }
}
