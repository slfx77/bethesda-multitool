using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="QuestRecord" /> (QUST) as PC-format subrecord bytes.
///     v6 emits the full record from scratch: EDID + SCRI? + FULL? + DATA + stage blocks
///     (INDX + QSDT? + CNAM?)* + objective blocks (QOBJ + NNAM?)*.
///     Override path is a no-op — quest definitions don't mutate at runtime in a way that
///     round-trips meaningfully (stage states are runtime concerns, not ESM mutations).
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
        return new EncodedRecord { Subrecords = [], Warnings = [] };
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
        // first INDX. Per-stage and per-objective conditions are not modeled yet (v13's
        // parser only captures top-level), so all condition emission lives here.
        foreach (var condition in quest.Conditions)
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

        // Stages: INDX (2-byte little-endian on Xbox AND PC for QUST) then optional QSDT +
        // CNAM. INDX comes before its associated QSDT/CNAM; the parser uses INDX to switch
        // between consecutive stages.
        foreach (var stage in quest.Stages)
        {
            var indx = new byte[2];
            SubrecordEncoder.WriteInt16(indx, 0, (short)stage.Index);
            subs.Add(new EncodedSubrecord("INDX", indx));

            if (stage.Flags != 0)
            {
                subs.Add(NewRecordSubrecords.EncodeByteSubrecord("QSDT", stage.Flags));
            }

            if (!string.IsNullOrEmpty(stage.LogEntry))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CNAM", stage.LogEntry));
            }
        }

        // Objectives: QOBJ (4-byte int32 index) then optional NNAM (display text).
        foreach (var objective in quest.Objectives)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("QOBJ", objective.Index));

            if (!string.IsNullOrEmpty(objective.DisplayText))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NNAM", objective.DisplayText));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
