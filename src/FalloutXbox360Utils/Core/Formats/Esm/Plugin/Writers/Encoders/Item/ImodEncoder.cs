using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="WeaponModRecord" /> (IMOD) as PC-format subrecord bytes.
///     FNV-specific record type for weapon modifications.
///     New-record-only path: override emission is a no-op (master ESM bytes retained verbatim).
///     fopdoc canonical order: EDID, OBND?, FULL?, DESC?, MODL?, ICON?, DATA.
///     DATA layout (8 bytes): int32 Value(0) + float Weight(4).
/// </summary>
public sealed class ImodEncoder : IRecordEncoder
{
    public string RecordType => "IMOD";
    public Type ModelType => typeof(WeaponModRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(WeaponModRecord imod)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(imod.EditorId))
        {
            warnings.Add($"New IMOD 0x{imod.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", imod.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(imod.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", imod.FullName));
        }

        if (!string.IsNullOrEmpty(imod.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", imod.Description));
        }

        if (!string.IsNullOrEmpty(imod.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", imod.ModelPath));
        }

        if (!string.IsNullOrEmpty(imod.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", imod.Icon));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(imod)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(WeaponModRecord imod)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteInt32(data, 0, imod.Value);
        SubrecordEncoder.WriteFloat(data, 4, imod.Weight);
        return data;
    }
}
