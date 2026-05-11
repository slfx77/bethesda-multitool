using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ContainerRecord" /> (CONT) as PC-format subrecord bytes.
///     v7 emits the full record from scratch: EDID + OBND? + FULL? + MODL? + SCRI? +
///     CNTO+ (per item) + DATA(5B Flags + Weight).
///     Override path is a no-op.
///     DATA layout (5 bytes, packed/unaligned):
///         byte  Flags(0)
///         float Weight(1) — little-endian
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
    ///     EDID, OBND, FULL, MODL, SCRI, CNTO+, DATA. SNAM/QNAM/RNAM deferred to v8.
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

        // ContainerRecord has no Bounds field — OBND not emitted.

        if (!string.IsNullOrEmpty(cont.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", cont.FullName));
        }

        if (!string.IsNullOrEmpty(cont.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", cont.ModelPath));
        }

        if (cont.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", cont.Script.Value));
        }

        foreach (var item in cont.Contents)
        {
            subs.Add(new EncodedSubrecord("CNTO", BuildCntoSubrecord(item)));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(cont)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ContainerRecord cont)
    {
        var data = new byte[5];
        data[0] = cont.Flags;
        SubrecordEncoder.WriteFloat(data, 1, cont.Weight);
        return data;
    }

    private static byte[] BuildCntoSubrecord(Models.InventoryItem item)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteFormId(data, 0, item.ItemFormId);
        SubrecordEncoder.WriteInt32(data, 4, item.Count);
        return data;
    }
}
