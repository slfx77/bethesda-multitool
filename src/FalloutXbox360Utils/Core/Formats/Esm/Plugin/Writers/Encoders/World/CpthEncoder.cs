using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="CameraPathRecord" /> (CPTH) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, [CTDA]*, ANAM(8B: parent + previous FormIDs),
///     DATA(1B flags), SNAM*(camera shot FormIDs).
///     CTDAs are emitted from <see cref="CameraPathRecord.Conditions" /> when populated,
///     run through <see cref="ConditionSanitizer" /> to drop/remap dangling FormID params.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class CpthEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<CameraPathRecord, object?>> AnamExtractors = new(StringComparer.Ordinal)
    {
        ["Parent"] = m => m.ParentPathFormId,
        ["Previous"] = m => m.PreviousPathFormId,
    };

    public string RecordType => "CPTH";
    public Type ModelType => typeof(CameraPathRecord);

    internal static EncodedRecord EncodeNew(
        CameraPathRecord cpth,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cpth.EditorId))
        {
            warnings.Add($"New CPTH 0x{cpth.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cpth.EditorId ?? string.Empty));

        // CTDAs gate when the camera path is eligible. Emit captured conditions through the
        // same sanitizer the INFO/IDLE encoders use, to drop/remap dangling FormID params.
        // Unlike IDLE, no never-fire fallback: CPTHs are camera shots (visual-only) so emitting
        // an unconditional path when no conditions were captured is benign.
        if (cpth.Conditions.Count > 0)
        {
            IReadOnlyList<Models.Records.Quest.DialogueCondition> emitConds;
            if (validFormIds is null)
            {
                emitConds = cpth.Conditions;
            }
            else
            {
                var ctdaDropped = 0;
                var ctdaRemapped = 0;
                emitConds = ConditionSanitizer.Filter(
                    cpth.Conditions, ToMutableSet(validFormIds), remapTable,
                    ref ctdaRemapped, ref ctdaDropped);
                if (ctdaDropped > 0 || ctdaRemapped > 0)
                {
                    warnings.Add(
                        $"New CPTH 0x{cpth.FormId:X8} CTDA sanitizer: dropped {ctdaDropped} " +
                        $"condition(s) with dangling FormID params, remapped {ctdaRemapped} " +
                        "param FormID(s) via the runtime→emitted alias table.");
                }
            }

            foreach (var cond in emitConds)
            {
                subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(cond)));
            }
        }
        else if (cpth.ConditionCount > 0)
        {
            warnings.Add(
                $"New CPTH 0x{cpth.FormId:X8}: {cpth.ConditionCount} CTDA condition(s) captured " +
                "in count but Conditions list is empty — emitting zero CTDAs.");
        }

        // ANAM: 8 bytes (parent FormID + previous FormID).
        subs.Add(SchemaModelSerializer.SerializeSubrecord("ANAM", "CPTH", 8, cpth, AnamExtractors));

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", cpth.Flags));

        foreach (var shotFormId in cpth.CameraShotFormIds)
        {
            if (shotFormId == 0)
            {
                continue;
            }
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", shotFormId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     ConditionSanitizer.Filter takes a mutable HashSet. Mirrors the adapter in
    ///     <see cref="IdleEncoder" />.
    /// </summary>
    private static HashSet<uint> ToMutableSet(IReadOnlySet<uint> set)
    {
        return set as HashSet<uint> ?? new HashSet<uint>(set);
    }
}
