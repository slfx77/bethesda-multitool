using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

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

        // DATA (8 bytes): byte Flags(0) + byte Priority(1) + pad(2..3) + float QuestDelay.
        // Single-occurrence; safe to replace.
        var data = new byte[8];
        data[0] = quest.Flags;
        data[1] = quest.Priority;
        SubrecordEncoder.WriteFloat(data, 4, quest.QuestDelay);
        subs.Add(new EncodedSubrecord("DATA", data));

        // If only DATA was emitted (no FULL mutation), fall through to empty so the
        // merge engine retains the master's QUST verbatim — DATA flags rarely differ.
        if (subs.Count == 1)
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
    /// </summary>
    internal static EncodedRecord EncodeNew(QuestRecord quest)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(quest.EditorId))
        {
            warnings.Add($"New QUST 0x{quest.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", quest.EditorId ?? string.Empty));

        BuildCanonicalSubrecords(quest, subs);
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
    ///     Emits the canonical QUST subrecord stream (everything except EDID). Shared
    ///     between the override path and the new-record path.
    /// </summary>
    private static void BuildCanonicalSubrecords(QuestRecord quest, List<EncodedSubrecord> subs)
    {
        if (quest.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", quest.Script.Value));
        }

        if (!string.IsNullOrEmpty(quest.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", quest.FullName));
        }

        var data = new byte[8];
        data[0] = quest.Flags;
        data[1] = quest.Priority;
        // bytes 2-3 padding
        SubrecordEncoder.WriteFloat(data, 4, quest.QuestDelay);
        subs.Add(new EncodedSubrecord("DATA", data));

        // Top-level quest conditions — CTDA* + optional CIS1/CIS2 between DATA and the
        // first INDX. Per-stage and per-target conditions are emitted within their owning
        // INDX / QSTA blocks below via the same helper.
        EmitConditions(subs, quest.Conditions);

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

            EmitConditions(subs, stage.Conditions);

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
                var qsta = new byte[8];
                SubrecordEncoder.WriteFormId(qsta, 0, target.TargetFormId);
                qsta[4] = target.Flags;
                // bytes 5-7 padding
                subs.Add(new EncodedSubrecord("QSTA", qsta));

                EmitConditions(subs, target.Conditions);
            }
        }
    }

    /// <summary>
    ///     Emit CTDA + optional CIS1/CIS2 for each condition. Shared between top-level,
    ///     per-stage, and per-target condition lists.
    /// </summary>
    private static void EmitConditions(List<EncodedSubrecord> subs, List<DialogueCondition> conditionList)
    {
        foreach (var condition in conditionList)
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
}
