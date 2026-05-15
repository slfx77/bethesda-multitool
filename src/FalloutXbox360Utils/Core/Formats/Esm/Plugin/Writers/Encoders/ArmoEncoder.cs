using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="ArmorRecord" /> as PC-format ARMO subrecord bytes.
///     v1 emits DATA (12 bytes: int32 value + int32 health + float weight) only. DNAM
///     (DR/DT armor stats), BMDT (biped flags), ETYP (equipment type) are retained from the
///     source ESM — the parsed model fields cover only a subset of those subrecords' bytes,
///     so reconstructing them risks corrupting unmapped fields.
///     DATA layout: int32 Value(0) + int32 Health(4) + float Weight(8).
/// </summary>
public sealed class ArmoEncoder : IRecordEncoder
{
    public string RecordType => "ARMO";
    public Type ModelType => typeof(ArmorRecord);

    public EncodedRecord Encode(object model)
    {
        var armo = (ArmorRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(armo))],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new ARMO record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, BMDT, MODL?, MODT?, ICON?, MICO?, ETYP?, DATA, DNAM.
    ///     BMDT must appear before MODL — placing it after MODL trips the FNV runtime's
    ///     post-model BMDT_ID chunk table (max=4) instead of the pre-model one (max=8),
    ///     causing GeneralFlags to be truncated.
    /// </summary>
    /// <remarks>
    ///     DNAM (12 bytes) per the schema: int16 DR + 2 padding + float DT + 4 unknown bytes.
    ///     BMDT (8 bytes) per the schema: uint32 BipedFlags + uint8 GeneralFlags + 3 padding.
    /// </remarks>
    internal static EncodedRecord EncodeNew(ArmorRecord armo)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(armo.EditorId))
        {
            warnings.Add($"New ARMO 0x{armo.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", armo.EditorId ?? string.Empty));

        if (armo.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(armo.Bounds));
        }

        if (!string.IsNullOrEmpty(armo.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", armo.FullName));
        }

        // BMDT is 4 bytes in the FNV runtime's chunk table even though vanilla files emit 8.
        // Writing 8 triggers "Chunk size 8 too big in chunk BMDT_ID — Max size is 4" warnings
        // for every new ARMO record. The extra 4 bytes (uint8 GeneralFlags + 3 pad) get
        // truncated by the engine anyway, so dropping them costs the GeneralFlags byte
        // (PowerArmor/HeavyArmor classification) but the engine wasn't using it from the
        // file regardless. Net result: cleaner load log, same runtime behavior.
        var bmdt = new byte[4];
        SubrecordEncoder.WriteUInt32(bmdt, 0, armo.BipedFlags);
        subs.Add(new EncodedSubrecord("BMDT", bmdt));

        if (!string.IsNullOrEmpty(armo.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", armo.ModelPath));
        }
        else
        {
            warnings.Add($"New ARMO 0x{armo.FormId:X8} has no model path — armor won't render in-game.");
        }

        if (armo.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(armo.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", armo.IconPath));
        }

        if (!string.IsNullOrEmpty(armo.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", armo.MessageIconPath));
        }

        if (armo.EquipmentType != EquipmentType.None)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("ETYP", (int)armo.EquipmentType));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(armo)));

        // DNAM — defense data: DR (int16) + padding(2) + DT (float) + 4 unknown bytes (zero).
        var dnam = new byte[12];
        SubrecordEncoder.WriteInt16(dnam, 0, (short)armo.DamageResistance);
        // bytes 2-3 padding
        SubrecordEncoder.WriteFloat(dnam, 4, armo.DamageThreshold);
        // bytes 8-11 unknown (zero)
        subs.Add(new EncodedSubrecord("DNAM", dnam));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ArmorRecord armo)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteInt32(data, 0, armo.Value);
        SubrecordEncoder.WriteInt32(data, 4, armo.Health);
        SubrecordEncoder.WriteFloat(data, 8, armo.Weight);
        return data;
    }
}
