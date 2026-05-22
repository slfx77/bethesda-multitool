using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRecipe (RCPE, FormType 0x6A).
///     Reads full name, skill/level requirements, category pointers, and walks
///     BSSimpleList for ingredients and outputs — all via the PDB layout.
/// </summary>
internal sealed class RuntimeRecipeReader(RuntimeMemoryContext context)
{
    private const byte RcpeFormType = 0x6A;
    private const int MaxListNodes = 32;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    public RecipeRecord? ReadRuntimeRecipe(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != RcpeFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        // RECIPE_DATA at view.Offset("data", "TESRecipe"): Skill (int32) + Level (uint32) + ...
        var dataOff = view.Offset("data", "TESRecipe");
        if (dataOff is not { } d || d + 8 > view.Buffer.Length)
        {
            return null;
        }

        var skill = RuntimeMemoryContext.ReadInt32BE(view.Buffer, d);
        var level = RuntimeMemoryContext.ReadInt32BE(view.Buffer, d + 4);

        var ingredients = WalkComponentList(view, "ingredientlist");
        var outputs = WalkComponentList(view, "outputlist");

        return new RecipeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            RequiredSkill = skill,
            RequiredSkillLevel = level,
            CategoryFormId = view.FormIdPointer("pRecipeCat", "TESRecipe") ?? 0u,
            SubcategoryFormId = view.FormIdPointer("pRecipeSubCat", "TESRecipe") ?? 0u,
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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Walks BSSimpleList&lt;TESRecipeComponent*&gt; rooted at the named list field.
    ///     Each TESRecipeComponent: pItem (TESForm*, 4B) + uiCount (uint32, 4B).
    /// </summary>
    private List<(uint FormId, uint Count)> WalkComponentList(PdbStructView view, string fieldName)
    {
        var result = new List<(uint, uint)>();
        var listOff = view.Offset(fieldName, "TESRecipe");
        if (listOff is not { } o)
        {
            return result;
        }

        foreach (var componentVa in _context.WalkInlineBSSimpleListItemPointers(view.Buffer, o, MaxListNodes))
        {
            if (!_context.IsValidPointer(componentVa))
            {
                continue;
            }

            var compFileOffset = _context.VaToFileOffset(componentVa);
            if (compFileOffset == null)
            {
                continue;
            }

            var compBuffer = _context.ReadBytes(compFileOffset.Value, 8);
            if (compBuffer == null)
            {
                continue;
            }

            var itemVa = BinaryUtils.ReadUInt32BE(compBuffer);
            var count = BinaryUtils.ReadUInt32BE(compBuffer, 4);
            if (itemVa == 0 || count is 0 or > 1000)
            {
                continue;
            }

            var itemFormId = _context.FollowPointerVaToFormId(itemVa);
            if (itemFormId is > 0)
            {
                result.Add((itemFormId.Value, count));
            }
        }

        return result;
    }
}
