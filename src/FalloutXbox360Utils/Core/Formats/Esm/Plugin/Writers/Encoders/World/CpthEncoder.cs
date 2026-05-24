using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="CameraPathRecord" /> (CPTH) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, [CTDA]*, ANAM(8B: parent + previous FormIDs),
///     DATA(1B flags), SNAM*(camera shot FormIDs).
///     Our model only captures the CTDA count, not the individual conditions — emit zero CTDAs
///     and warn that conditions are deferred to ESM-side data.
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

    internal static EncodedRecord EncodeNew(CameraPathRecord cpth)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cpth.EditorId))
        {
            warnings.Add($"New CPTH 0x{cpth.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cpth.EditorId ?? string.Empty));

        if (cpth.ConditionCount > 0)
        {
            warnings.Add(
                $"New CPTH 0x{cpth.FormId:X8}: {cpth.ConditionCount} CTDA condition(s) captured in count " +
                "but individual conditions not modeled — emitting zero CTDAs.");
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
}
