using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ContainerRecord" /> (CONT) as PC-format subrecord bytes.
///     Emits the full record: EDID + OBND? + FULL? + MODL? + MODT? + SCRI? +
///     CNTO+COED?+ (per item) + DATA + SNAM? + QNAM? + RNAM?.
///     Override path is a no-op.
///     DATA layout (5 bytes, packed/unaligned):
///     byte  Flags(0)
///     float Weight(1) — little-endian
///     COED layout (12 bytes, optional per CNTO):
///     FormID Owner(0) + uint32 GlobalOrRank(4) + float ItemCondition(8)
/// </summary>
public sealed class ContEncoder : IRecordEncoder
{
    public string RecordType => "CONT";
    public Type ModelType => typeof(ContainerRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new CONT record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, MODT, SCRI, [CNTO+COED?]+, DATA, SNAM, QNAM, RNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(ContainerRecord cont)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cont.EditorId))
        {
            warnings.Add($"New CONT 0x{cont.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cont.EditorId ?? string.Empty));

        if (cont.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(cont.Bounds));
        }

        if (!string.IsNullOrEmpty(cont.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", cont.FullName));
        }

        if (!string.IsNullOrEmpty(cont.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", cont.ModelPath));
        }

        if (cont.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (cont.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", cont.Script.Value));
        }

        foreach (var item in cont.Contents)
        {
            subs.Add(new EncodedSubrecord("CNTO", BuildCntoSubrecord(item)));
            if (HasOwnership(item))
            {
                subs.Add(new EncodedSubrecord("COED", BuildCoedSubrecord(item)));
            }
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(cont)));

        if (cont.OpenSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", cont.OpenSoundFormId.Value));
        }

        if (cont.OpenSoundLoopFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QNAM", cont.OpenSoundLoopFormId.Value));
        }

        if (cont.CloseSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", cont.CloseSoundFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ContainerRecord cont)
    {
        var data = new byte[5];
        data[0] = cont.Flags;
        SubrecordEncoder.WriteFloat(data, 1, cont.Weight);
        return data;
    }

    private static byte[] BuildCntoSubrecord(InventoryItem item)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteFormId(data, 0, item.ItemFormId);
        SubrecordEncoder.WriteInt32(data, 4, item.Count);
        return data;
    }

    internal static bool HasOwnership(InventoryItem item)
    {
        return item.OwnerFormId.HasValue || item.GlobalOrRank.HasValue || item.ItemCondition.HasValue;
    }

    internal static byte[] BuildCoedSubrecord(InventoryItem item)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteFormId(data, 0, item.OwnerFormId ?? 0);
        SubrecordEncoder.WriteUInt32(data, 4, item.GlobalOrRank ?? 0);
        SubrecordEncoder.WriteFloat(data, 8, item.ItemCondition ?? 0f);
        return data;
    }
}
