using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="QuestRecord" /> (QUST) as PC-format subrecord bytes.
///     Both override and new-record paths emit EDID? + SCRI? + FULL? + DATA + CTDA* + stage
///     blocks + objective blocks. When the DMP captured any stages / objectives /
///     conditions / script / FULL the override emits the full canonical subrecord stream,
///     and the merge engine positionally replaces the master's blocks per signature.
///     When the DMP carries no quest content the override returns empty subrecords and
///     the engine retains the master verbatim.
///     Partial emission is unsafe: INDX + QSDT + CNAM (per stage) and QOBJ + NNAM + QSTA
///     (per objective) are positional groups, and the merge engine does per-signature
///     replacement only — emitting a subset would desynchronize the stage/objective tuples.
///     DATA (8 bytes): byte Flags(0) + byte Priority(1) + pad(2..3) + float QuestDelay(4..7).
///     INDX in QUST is little-endian on Xbox 360 (per <see cref="Conversion.Schema.SubrecordDialogueSchemas" />)
///     so the PC value is the same 2 bytes.
/// </summary>
public sealed class QustEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<QuestRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Flags"] = m => m.Flags,
        ["Priority"] = m => m.Priority,
        ["QuestDelay"] = m => m.QuestDelay,
    };

    private static readonly Dictionary<string, Func<QuestObjectiveTarget, object?>> QstaExtractors = new(StringComparer.Ordinal)
    {
        ["Target"] = m => m.TargetFormId,
        ["Flags"] = m => m.Flags,
    };

    public string RecordType => "QUST";
    public Type ModelType => typeof(QuestRecord);

    public EncodedRecord Encode(object model)
    {
        var quest = (QuestRecord)model;

        // Override path emits ONLY override-safe singletons (FULL / SCRI / DATA). Multi-
        // occurrence subrecords — stage triplets (INDX + QSDT + CNAM), objective triplets
        // (QOBJ + NNAM + QSTA), and top-level CTDA — are excluded because the merge engine's
        // positional per-signature replacement would interleave DMP entries with the master's
        // tail and break stage indexing, objective ordering, and condition chains. The
        // Emitting these caused random quest failures in-game.
        if (!HasOverrideContent(quest))
        {
            return new EncodedRecord { Subrecords = [], Warnings = [] };
        }

        var subs = new List<EncodedSubrecord>();

        // SCRI emission is deliberately omitted from the override path. The merge engine's
        // Pass-2 step appends any encoder subrecord whose signature isn't present in master
        // — for quests where master lacks SCRI (e.g. VFreeformVault11 [QUST:000E8875]),
        // appending SCRI puts it after DATA, which FNVEdit flags as out-of-order. Adding
        // scripts to scriptless quests is out-of-scope for the DMP override path; if a quest
        // needs a new script, route it through EncodeNew or a dedicated diagnostic.

        if (!string.IsNullOrEmpty(quest.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", quest.FullName));
        }

        // DATA emission is deliberately omitted from the override path. The byte
        // contains:
        //   - byte 0: Flags — the runtime TESQuest struct co-locates ESM-canonical bits
        //     (StartGameEnabled 0x01, AllowRepeatedConversationTopics 0x04, AllowRepeated
        //     Stages 0x08) with engine-set state bits (Started, Active, Completed). The
        //     DMP capture sees the engine's CURRENT runtime state, not the authoring
        //     intent — emitting it as an override either (a) wipes master's StartGame
        //     Enabled bit (Doc Mitchell's VMS01 intro never auto-starts; player stuck
        //     in bed) or (b) carries the runtime Started bit (Sunny Smiles' "Back in
        //     the Saddle" appears already in progress before the player meets her).
        //     Both observed in the 2026-05-27 xex44 capture.
        //   - byte 1: Priority — authoring field; master's value is canonical.
        //   - bytes 4..7: QuestDelay — runtime can reset this between save loads.
        // Letting the merge engine retain master's DATA verbatim is strictly safer
        // than emitting a runtime snapshot. If a proto build legitimately needs a
        // different DATA byte, route it through EncodeNew or a dedicated diagnostic.
        if (subs.Count == 0)
        {
            return new EncodedRecord { Subrecords = [], Warnings = [] };
        }

        return new EncodedRecord { Subrecords = subs, Warnings = [] };
    }

    /// <summary>
    ///     Encode a new QUST record from scratch in fopdoc canonical order:
    ///     EDID, SCRI?, FULL?, ICON?, DATA, CTDA*, then per stage:
    ///     INDX + (QSDT + CNAM?)*, then per objective: QOBJ + NNAM? + QSTA*.
    ///     Quest stages are emitted in index order; objectives are emitted in index order.
    ///     <para>
    ///     CTDAs across top-level + per-stage + per-objective-target are routed through
    ///     <see cref="ConditionSanitizer.Filter" /> so dangling FormID parameters get remapped
    ///     via the runtime→emitted alias table or dropped (same policy as INFO). When
    ///     <paramref name="validFormIds" /> is null (legacy callers / tests) we skip sanitization
    ///     and emit conditions verbatim.
    ///     </para>
    /// </summary>
    internal static EncodedRecord EncodeNew(
        QuestRecord quest,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(quest.EditorId))
        {
            warnings.Add($"New QUST 0x{quest.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", quest.EditorId ?? string.Empty));

        var droppedCtdas = 0;
        var remappedCtdaParams = 0;
        BuildCanonicalSubrecords(quest, subs, validFormIds, remapTable,
            ref droppedCtdas, ref remappedCtdaParams);
        if (droppedCtdas > 0 || remappedCtdaParams > 0)
        {
            warnings.Add(
                $"New QUST 0x{quest.FormId:X8} CTDA sanitizer: dropped {droppedCtdas} condition(s) " +
                $"with dangling FormID params, remapped {remappedCtdaParams} param FormID(s) via " +
                "the runtime→emitted alias table.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Returns true when the DMP-parsed model carries any meaningful QUST content that
    ///     should reach the output ESP. Used by the override path to short-circuit empty
    ///     records so the merge engine retains the master verbatim.
    /// </summary>
    private static bool HasOverrideContent(QuestRecord quest)
    {
        return quest.Stages.Count > 0
               || quest.Objectives.Count > 0
               || quest.Conditions.Count > 0
               || quest.Script.HasValue
               || !string.IsNullOrEmpty(quest.FullName);
    }

    /// <summary>
    ///     Emits the canonical QUST subrecord stream (everything except EDID). Called only
    ///     from the new-record path; CTDAs are routed through
    ///     <see cref="ConditionSanitizer.Filter" /> when <paramref name="validFormIds" /> is
    ///     supplied.
    /// </summary>
    private static void BuildCanonicalSubrecords(
        QuestRecord quest,
        List<EncodedSubrecord> subs,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int droppedCtdas,
        ref int remappedCtdaParams)
    {
        if (quest.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", quest.Script.Value));
        }

        if (!string.IsNullOrEmpty(quest.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", quest.FullName));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "QUST", 8, quest, DataExtractors));

        // Top-level quest conditions — CTDA* + optional CIS1/CIS2 between DATA and the
        // first INDX. Per-stage and per-target conditions are emitted within their owning
        // INDX / QSTA blocks below via the same helper.
        EmitConditions(subs, quest.Conditions, validFormIds, remapTable,
            ref droppedCtdas, ref remappedCtdaParams);

        // Stages: INDX (2-byte little-endian on Xbox AND PC for QUST) then optional QSDT +
        // stage CTDA* + CIS1/CIS2 + CNAM. Stage CTDA gates when the log entry triggers.
        foreach (var stage in quest.Stages)
        {
            var indx = new byte[2];
            SubrecordEncoder.WriteInt16(indx, 0, (short)stage.Index);
            subs.Add(new EncodedSubrecord("INDX", indx));

            if (stage.Flags != 0)
            {
                subs.Add(NewRecordSubrecords.EncodeByteSubrecord("QSDT", stage.Flags));
            }

            EmitConditions(subs, stage.Conditions, validFormIds, remapTable,
                ref droppedCtdas, ref remappedCtdaParams);

            if (!string.IsNullOrEmpty(stage.LogEntry))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CNAM", stage.LogEntry));
            }
        }

        // Objectives: QOBJ (4-byte int32 index) + optional NNAM + per-target QSTA + CTDA*.
        foreach (var objective in quest.Objectives)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("QOBJ", objective.Index));

            if (!string.IsNullOrEmpty(objective.DisplayText))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NNAM", objective.DisplayText));
            }

            foreach (var target in objective.Targets)
            {
                subs.Add(SchemaModelSerializer.SerializeSubrecord("QSTA", "", 8, target, QstaExtractors));

                EmitConditions(subs, target.Conditions, validFormIds, remapTable,
                    ref droppedCtdas, ref remappedCtdaParams);
            }
        }
    }

    /// <summary>
    ///     Emit CTDA + optional CIS1/CIS2 for each condition. Shared between top-level,
    ///     per-stage, and per-target condition lists. When <paramref name="validFormIds" /> is
    ///     supplied, conditions with dangling FormID parameters are remapped or dropped
    ///     via <see cref="ConditionSanitizer.Filter" />. The CIS1/CIS2 string
    ///     subrecords follow their CTDA only when the CTDA itself survives sanitization.
    /// </summary>
    private static void EmitConditions(
        List<EncodedSubrecord> subs,
        List<DialogueCondition> conditionList,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int droppedCtdas,
        ref int remappedCtdaParams)
    {
        IReadOnlyList<DialogueCondition> emitConds;
        if (validFormIds is null)
        {
            emitConds = conditionList;
        }
        else
        {
            emitConds = ConditionSanitizer.Filter(
                conditionList, ToMutableSet(validFormIds), remapTable,
                ref remappedCtdaParams, ref droppedCtdas);
        }

        foreach (var condition in emitConds)
        {
            subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
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

    private static HashSet<uint> ToMutableSet(IReadOnlySet<uint> set)
    {
        return set as HashSet<uint> ?? new HashSet<uint>(set);
    }
}
