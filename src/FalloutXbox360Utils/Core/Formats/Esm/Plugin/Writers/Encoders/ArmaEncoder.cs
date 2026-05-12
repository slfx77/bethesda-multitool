using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="ArmaRecord" /> (Armor Addon) as PC-format subrecord bytes.
///     ARMA defines the visual model for an armor piece, with separate male/female and
///     third/first-person mesh paths. New-record-only path: override emission is a no-op.
///     fopdoc canonical order: EDID, OBND?, FULL?, BMDT, MODL?, MOD2?, MOD3?, MOD4?, DATA.
///     BMDT layout (8 bytes): uint32 BipedFlags(0) + uint8 GeneralFlags(4) + 3 padding.
///     DATA layout (12 bytes): int32 Value(0) + int32 MaxCondition(4) + float Weight(8).
/// </summary>
public sealed class ArmaEncoder : IRecordEncoder
{
    public string RecordType => "ARMA";
    public Type ModelType => typeof(ArmaRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ArmaRecord arma)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(arma.EditorId))
        {
            warnings.Add($"New ARMA 0x{arma.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", arma.EditorId ?? string.Empty));

        if (arma.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(arma.Bounds));
        }

        if (!string.IsNullOrEmpty(arma.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", arma.FullName));
        }

        // BMDT — required for the engine to know which biped slots this addon covers.
        var bmdt = new byte[8];
        SubrecordEncoder.WriteUInt32(bmdt, 0, arma.BipedFlags);
        bmdt[4] = arma.GeneralFlags;
        // bytes 5-7 padding (zero)
        subs.Add(new EncodedSubrecord("BMDT", bmdt));

        if (!string.IsNullOrEmpty(arma.MaleModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", arma.MaleModelPath));
        }

        if (!string.IsNullOrEmpty(arma.FemaleModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD2", arma.FemaleModelPath));
        }

        if (!string.IsNullOrEmpty(arma.MaleFirstPersonModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD3", arma.MaleFirstPersonModelPath));
        }

        if (!string.IsNullOrEmpty(arma.FemaleFirstPersonModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD4", arma.FemaleFirstPersonModelPath));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(arma)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ArmaRecord arma)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteInt32(data, 0, arma.Value);
        SubrecordEncoder.WriteInt32(data, 4, arma.MaxCondition);
        SubrecordEncoder.WriteFloat(data, 8, arma.Weight);
        return data;
    }
}
