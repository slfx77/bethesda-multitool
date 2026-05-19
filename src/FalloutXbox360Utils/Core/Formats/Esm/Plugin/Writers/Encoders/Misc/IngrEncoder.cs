using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an <see cref="IngredientRecord" /> (INGR) as PC-format subrecord bytes.
///     INGR is a legacy Oblivion-era ingredient form; FNV retains a single INGR record but
///     mostly uses ALCH for consumables. fopdoc canonical INGR layout includes EDID, FULL,
///     MODL, ETYP(equip type FormID), DATA(8B: int32 value + float weight), ENIT, EFID/EFIT
///     effect chain. Our model only carries weight + equip type — emit those and a placeholder
///     DATA with value=0; warn that effect-chain emission is deferred.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class IngrEncoder : IRecordEncoder
{
    public string RecordType => "INGR";
    public Type ModelType => typeof(IngredientRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(IngredientRecord ingr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ingr.EditorId))
        {
            warnings.Add($"New INGR 0x{ingr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ingr.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(ingr.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", ingr.FullName));
        }

        if (!string.IsNullOrEmpty(ingr.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", ingr.ModelPath));
        }

        if (ingr.EquipType != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ETYP", ingr.EquipType));
        }

        // DATA: 8 bytes (int32 value + float weight). Model only carries weight; emit value=0.
        var data = new byte[8];
        SubrecordEncoder.WriteInt32(data, 0, 0);
        SubrecordEncoder.WriteFloat(data, 4, ingr.Weight);
        subs.Add(new EncodedSubrecord("DATA", data));

        warnings.Add(
            $"New INGR 0x{ingr.FormId:X8}: ENIT/EFID/EFIT effect chain not modeled — deferred.");

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
