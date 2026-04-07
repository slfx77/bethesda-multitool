using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRecipe (RCPE, 108 bytes, FormType 0x6A).
///     Reads full name, skill/level requirements, category pointers,
///     and walks BSSimpleList for ingredients and outputs.
/// </summary>
internal sealed class RuntimeRecipeReader
{
    private const int MaxListNodes = 32;
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

        // Walk ingredient and output BSSimpleLists
        var ingredients = WalkRecipeComponentList(buffer, IngredientListOffset);
        var outputs = WalkRecipeComponentList(buffer, OutputListOffset);

        return new RecipeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            RequiredSkill = skill,
            RequiredSkillLevel = level,
            CategoryFormId = categoryFormId,
            SubcategoryFormId = subcategoryFormId,
            Ingredients = ingredients.Select(c => new RecipeIngredient
            {
                ItemFormId = c.FormId,
                Count = c.Count
            }).ToList(),
            Outputs = outputs.Select(c => new RecipeOutput
            {
                ItemFormId = c.FormId,
                Count = c.Count
            }).ToList(),
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Walk a BSSimpleList of TESRecipeComponent* pointers.
    ///     Each node: m_item (TESRecipeComponent*, 4B) + m_pkNext (BSSimpleList*, 4B).
    ///     TESRecipeComponent layout: pItem (TESForm*, 4B) + uiCount (uint32, 4B).
    /// </summary>
    private List<(uint FormId, uint Count)> WalkRecipeComponentList(byte[] structBuffer, int listOffset)
    {
        var result = new List<(uint, uint)>();

        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        if (headVa == 0 || !_context.IsValidPointer(headVa))
        {
            return result;
        }

        var visited = new HashSet<uint>();
        var currentVa = headVa;

        for (var i = 0; i < MaxListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            // BSSimpleList node: m_item (4B pointer to TESRecipeComponent) + m_pkNext (4B)
            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var componentVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (componentVa != 0 && _context.IsValidPointer(componentVa))
            {
                var compFileOffset = _context.VaToFileOffset(componentVa);
                if (compFileOffset != null)
                {
                    // TESRecipeComponent: pItem (TESForm*, 4B) + uiCount (uint32, 4B)
                    var compBuffer = _context.ReadBytes(compFileOffset.Value, 8);
                    if (compBuffer != null)
                    {
                        var itemVa = BinaryUtils.ReadUInt32BE(compBuffer);
                        var count = BinaryUtils.ReadUInt32BE(compBuffer, 4);

                        if (itemVa != 0 && count is > 0 and <= 1000)
                        {
                            var itemFormId = _context.FollowPointerVaToFormId(itemVa);
                            if (itemFormId is > 0)
                            {
                                result.Add((itemFormId.Value, count));
                            }
                        }
                    }
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    #region Constants

    private const byte RcpeFormType = 0x6A;
    private const int StructSize = 108;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44; // TESFullName.cFullName BSStringT
    private const int RecipeDataOffset = 52; // RECIPE_DATA: Skill(4) + Level(4) + ...
    private const int IngredientListOffset = 76; // BSSimpleList<TESRecipeComponent*>
    private const int OutputListOffset = 84; // BSSimpleList<TESRecipeComponent*>
    private const int CategoryPtrOffset = 100; // pRecipeCat (pointer to TESRecipeCategory)
    private const int SubcategoryPtrOffset = 104; // pRecipeSubCat (pointer to TESRecipeCategory)

    #endregion
}
