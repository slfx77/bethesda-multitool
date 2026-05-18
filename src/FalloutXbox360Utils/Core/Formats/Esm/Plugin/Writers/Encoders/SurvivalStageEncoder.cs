using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a hardcore-mode survival stage record (RADS/DEHY/HUNG/SLPD) as PC-format
///     subrecord bytes. All four record types share the same layout — one encoder serves
///     all four signatures via multi-registration (mirrors how LvliEncoder handles
///     LVLI/LVLN/LVLC).
///     fopdoc canonical order: EDID, DATA(8B: uint32 threshold + uint32 modifier).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class SurvivalStageEncoder : IRecordEncoder
{
    public string RecordType => "RADS";
    public Type ModelType => typeof(SurvivalStageRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(SurvivalStageRecord stage)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(stage.EditorId))
        {
            warnings.Add($"New survival-stage record 0x{stage.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", stage.EditorId ?? string.Empty));
        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(stage)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(SurvivalStageRecord stage)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteUInt32(data, 0, stage.Threshold);
        SubrecordEncoder.WriteUInt32(data, 4, stage.Modifier);
        return data;
    }
}
