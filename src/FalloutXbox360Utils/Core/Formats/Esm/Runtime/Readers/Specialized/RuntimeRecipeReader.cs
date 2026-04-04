using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRecipe (RCPE, 108 bytes, FormType 0x6A).
///     Reads full name, skill/level requirements, and category pointers.
///     Ingredient and output lists are ESM-only (BSSimpleList walking not justified).
/// </summary>
internal sealed class RuntimeRecipeReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeRecipeReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public RecipeRecord? ReadRuntimeRecipe(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RcpeFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);

        // RECIPE_DATA at +52: Skill (int32) + Level (uint32)
        var skill = RuntimeMemoryContext.ReadInt32BE(buffer, RecipeDataOffset);
        var level = RuntimeMemoryContext.ReadInt32BE(buffer, RecipeDataOffset + 4);

        // Follow category pointers to get FormIDs
        var categoryFormId = _context.FollowPointerToFormId(buffer, CategoryPtrOffset) ?? 0u;
        var subcategoryFormId = _context.FollowPointerToFormId(buffer, SubcategoryPtrOffset) ?? 0u;

        return new RecipeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            RequiredSkill = skill,
            RequiredSkillLevel = level,
            CategoryFormId = categoryFormId,
            SubcategoryFormId = subcategoryFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte RcpeFormType = 0x6A;
    private const int StructSize = 108;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44; // TESFullName.cFullName BSStringT
    private const int RecipeDataOffset = 52; // RECIPE_DATA: Skill(4) + Level(4) + ...
    private const int CategoryPtrOffset = 100; // pRecipeCat (pointer to TESRecipeCategory)
    private const int SubcategoryPtrOffset = 104; // pRecipeSubCat (pointer to TESRecipeCategory)

    #endregion
}
