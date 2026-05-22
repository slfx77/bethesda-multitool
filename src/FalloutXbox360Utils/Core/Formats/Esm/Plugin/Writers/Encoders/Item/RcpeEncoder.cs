using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="RecipeRecord" /> (RCPE) as PC-format subrecord bytes.
///     FNV crafting recipe — links input ingredients to output items.
///     New-record-only path: override emission is a no-op.
///     fopdoc canonical order: EDID, FULL?, CTDA*, DATA, (RCIL + RCQY)*, (RCOD + RCQY)*.
///     DATA layout (16 bytes): int32 Skill(0) + uint32 Level(4) + FormID Category(8) + FormID SubCategory(12).
///     RCIL/RCOD = 4-byte item FormID; RCQY = uint32 count following its paired RCIL/RCOD.
/// </summary>
public sealed class RcpeEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<RecipeRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Skill"] = m => m.RequiredSkill,
        ["Level"] = m => (uint)m.RequiredSkillLevel,
        ["Category"] = m => m.CategoryFormId,
        ["SubCategory"] = m => m.SubcategoryFormId,
    };

    public string RecordType => "RCPE";
    public Type ModelType => typeof(RecipeRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(RecipeRecord recipe)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(recipe.EditorId))
        {
            warnings.Add($"New RCPE 0x{recipe.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", recipe.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(recipe.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", recipe.FullName));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "RCPE", 16, recipe, DataExtractors));

        foreach (var ingredient in recipe.Ingredients)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RCIL", ingredient.ItemFormId));
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("RCQY", ingredient.Count));
        }

        foreach (var output in recipe.Outputs)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RCOD", output.ItemFormId));
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("RCQY", output.Count));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
