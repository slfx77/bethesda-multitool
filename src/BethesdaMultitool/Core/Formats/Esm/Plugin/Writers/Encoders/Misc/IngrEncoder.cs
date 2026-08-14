using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Partial diagnostic byte projection for an <see cref="IngredientRecord" /> (INGR).
///     FNV stores equipment type as an int32 enum and Weight as a four-byte DATA float, then
///     requires a separate eight-byte ENIT block and an effect group. The typed model does not
///     retain ENIT or effects, so <see cref="PlannedWriter.PlannedEncoders" /> deliberately does
///     not production-route this builder. It remains available to isolated format tests while
///     master records are preserved verbatim.
/// </summary>
public sealed class IngrEncoder : IRecordEncoder
{
    public string RecordType => "INGR";
    public Type ModelType => typeof(IngredientRecord);

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

        // ETYP is a required int32 enum, not a FormID. The partial model uses raw uint storage.
        subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("ETYP", unchecked((int)ingr.EquipType)));

        // FNV INGR DATA is exactly one float. Value and flags live in the separate ENIT block.
        var data = new byte[4];
        SubrecordEncoder.WriteFloat(data, 0, ingr.Weight);
        subs.Add(new EncodedSubrecord("DATA", data));

        warnings.Add(
            $"New INGR 0x{ingr.FormId:X8}: partial diagnostic projection only; " +
            "ENIT and the required effect group are not modeled, so production planning excludes INGR.");

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
