using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ChallengeRecord" /> (CHAL) as PC-format subrecord bytes.
///     FNV achievement-like goals.
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, SCRI?, DATA(24B).
///     DATA layout (24B per PDB CHALLENGE_DATA):
///         int32 ChallengeType(0) + int32 Threshold(4) + uint16 Flags(8) + pad(2) +
///         int32 Interval(12) + uint16 SpecialDataOne(16) + uint16 SpecialDataTwo(18) +
///         uint16 SpecialDataThree(20) + pad(2).
/// </summary>
public sealed class ChalEncoder : IRecordEncoder
{
    public string RecordType => "CHAL";
    public Type ModelType => typeof(ChallengeRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

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

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(chal)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ChallengeRecord chal)
    {
        var data = new byte[24];
        SubrecordEncoder.WriteInt32(data, 0, (int)chal.ChallengeType);
        SubrecordEncoder.WriteInt32(data, 4, (int)chal.Threshold);
        SubrecordEncoder.WriteUInt16(data, 8, (ushort)chal.Flags);
        // bytes 10-11 padding
        SubrecordEncoder.WriteInt32(data, 12, (int)chal.Interval);
        SubrecordEncoder.WriteUInt16(data, 16, (ushort)chal.Value1);
        SubrecordEncoder.WriteUInt16(data, 18, (ushort)chal.Value2);
        SubrecordEncoder.WriteUInt16(data, 20, (ushort)chal.Value3);
        // bytes 22-23 padding
        return data;
    }
}
