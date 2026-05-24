using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="ChallengeRecord" /> (CHAL) as PC-format subrecord bytes.
///     FNV achievement-like goals.
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, SCRI?, DATA(24B).
///     DATA layout (24B per PDB CHALLENGE_DATA):
///     int32 ChallengeType(0) + int32 Threshold(4) + uint16 Flags(8) + pad(2) +
///     int32 Interval(12) + uint16 SpecialDataOne(16) + uint16 SpecialDataTwo(18) +
///     uint16 SpecialDataThree(20) + pad(2).
/// </summary>
public sealed class ChalEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<ChallengeRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["ChallengeType"] = m => (int)m.ChallengeType,
        ["Threshold"] = m => (int)m.Threshold,
        ["Flags"] = m => (ushort)m.Flags,
        ["Interval"] = m => (int)m.Interval,
        ["SpecialDataOne"] = m => (ushort)m.Value1,
        ["SpecialDataTwo"] = m => (ushort)m.Value2,
        ["SpecialDataThree"] = m => (ushort)m.Value3,
    };

    public string RecordType => "CHAL";
    public Type ModelType => typeof(ChallengeRecord);

    internal static EncodedRecord EncodeNew(ChallengeRecord chal)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(chal.EditorId))
        {
            warnings.Add($"New CHAL 0x{chal.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", chal.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(chal.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", chal.FullName));
        }

        if (!string.IsNullOrEmpty(chal.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", chal.Description));
        }

        if (!string.IsNullOrEmpty(chal.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", chal.Icon));
        }

        if (chal.Script != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", chal.Script));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "CHAL", 24, chal, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
