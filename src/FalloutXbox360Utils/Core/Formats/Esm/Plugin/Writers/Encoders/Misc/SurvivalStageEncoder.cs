using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

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
    // The 4 survival record types (RADS/DEHY/HUNG/SLPD) all share an identical 8-byte DATA
    // schema, so we serialize using the RADS schema regardless of which type is being emitted.
    private static readonly Dictionary<string, Func<SurvivalStageRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["TriggerThreshold"] = m => m.Threshold,
        ["ActorEffect"] = m => m.Modifier,
    };

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
        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "RADS", 8, stage, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
