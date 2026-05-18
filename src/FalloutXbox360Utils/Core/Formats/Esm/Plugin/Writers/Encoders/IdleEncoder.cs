using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="IdleAnimationRecord" /> (IDLE) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, MODL?, MODT?, [CTDA]*, ANAM(8B: parent + previous FormIDs),
///     DATA(8B: animData byte + loopMin + loopMax + unknown + replayDelay u16 + flagsEx + pad).
///     Our model only captures the CTDA count, not the individual conditions — emit zero CTDAs
///     and warn that conditions are deferred to ESM-side data.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class IdleEncoder : IRecordEncoder
{
    public string RecordType => "IDLE";
    public Type ModelType => typeof(IdleAnimationRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(IdleAnimationRecord idle)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(idle.EditorId))
        {
            warnings.Add($"New IDLE 0x{idle.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", idle.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(idle.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", idle.ModelPath));
        }

        if (idle.ConditionCount > 0)
        {
            warnings.Add(
                $"New IDLE 0x{idle.FormId:X8}: {idle.ConditionCount} CTDA condition(s) captured in count " +
                "but individual conditions not modeled — emitting zero CTDAs.");
        }

        // ANAM: 8 bytes (parent FormID + previous FormID).
        var anam = new byte[8];
        SubrecordEncoder.WriteFormId(anam, 0, idle.ParentIdleFormId);
        SubrecordEncoder.WriteFormId(anam, 4, idle.PreviousIdleFormId);
        subs.Add(new EncodedSubrecord("ANAM", anam));

        // DATA: 8 bytes packed per FNV IDLE schema.
        var data = new byte[8];
        data[0] = idle.AnimData;
        data[1] = idle.LoopMin;
        data[2] = idle.LoopMax;
        data[3] = 0; // unknown / reserved
        SubrecordEncoder.WriteUInt16(data, 4, idle.ReplayDelay);
        data[6] = idle.FlagsEx;
        // data[7] = 0 pad
        subs.Add(new EncodedSubrecord("DATA", data));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
