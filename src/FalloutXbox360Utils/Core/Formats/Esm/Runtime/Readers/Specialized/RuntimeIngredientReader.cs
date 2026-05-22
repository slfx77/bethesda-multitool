using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for IngredientItem (INGR, FormType 0x1D).
///     Legacy Oblivion-era ingredient — FNV only has 1 record but provides
///     forward-compatibility.
/// </summary>
internal sealed class RuntimeIngredientReader(RuntimeMemoryContext context)
{
    private const byte IngrFormType = 0x1D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public IngredientRecord? ReadRuntimeIngredient(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != IngrFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var weight = view.Float("fWeight", "TESWeightForm");

        return new IngredientRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = view.BsString("cFullName", "TESFullName"),
            ModelPath = view.BsString("cModel", "TESModel"),
            Weight = RuntimeMemoryContext.IsNormalFloat(weight) ? weight : 0,
            EquipType = view.UInt32("eEquipType", "BGSEquipType"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
