using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes an <see cref="EncounterZoneRecord" /> (ECZN) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, DATA(8B: FormID owner + sbyte rank + sbyte min level + byte flags + 1B pad).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class EczEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<EncounterZoneRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Owner"] = m => m.OwnerFormId,
        ["Rank"] = m => (sbyte)m.Rank,
        ["MinimumLevel"] = m => (sbyte)m.MinimumLevel,
        ["Flags"] = m => m.Flags,
    };

    public string RecordType => "ECZN";
    public Type ModelType => typeof(EncounterZoneRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(EncounterZoneRecord ecz)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ecz.EditorId))
        {
            warnings.Add($"New ECZN 0x{ecz.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ecz.EditorId ?? string.Empty));
        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "ECZN", 8, ecz, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
