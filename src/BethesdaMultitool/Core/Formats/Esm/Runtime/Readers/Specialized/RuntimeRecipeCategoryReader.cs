using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRecipeCategory (RCCT, FormType 0x6B).
///     Reads the display name and the 1-byte RECIPE_CATEGORY_DATA flags via the PDB layout.
/// </summary>
internal sealed class RuntimeRecipeCategoryReader(RuntimeMemoryContext context)
{
    private const byte RcctFormType = 0x6B;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime recipe-category record for the given DMP entry, or null if it can't be read.</summary>
    public RecipeCategoryRecord? ReadRuntimeRecipeCategory(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != RcctFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, RcctFormType);
        if (view == null)
        {
            return null;
        }

        return new RecipeCategoryRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = view.BsString("cFullName", "TESFullName"),
            Flags = view.Byte("data", "TESRecipeCategory"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
