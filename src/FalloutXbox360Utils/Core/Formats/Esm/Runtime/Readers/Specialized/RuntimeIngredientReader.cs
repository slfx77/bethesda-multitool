using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for IngredientItem (INGR, 180 bytes, FormType 0x1D).
///     Legacy Oblivion-era ingredient — FNV only has 1 record but provides
///     forward-compatibility.
/// </summary>
internal sealed class RuntimeIngredientReader(RuntimeMemoryContext context)
{
    public IngredientRecord? ReadRuntimeIngredient(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != IngrFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
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

        var fullName = context.ReadBsStringT(offset, FullNameOffset);
        var modelPath = context.ReadBsStringT(offset, ModelOffset);
        var weight = BinaryUtils.ReadFloatBE(buffer, WeightOffset);
        var equipType = BinaryUtils.ReadUInt32BE(buffer, EquipTypeOffset);

        return new IngredientRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Weight = RuntimeMemoryContext.IsNormalFloat(weight) ? weight : 0,
            EquipType = equipType,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte IngrFormType = 0x1D;
    private const int StructSize = 180;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 68;
    private const int ModelOffset = 96;
    private const int WeightOffset = 152;
    private const int EquipTypeOffset = 160;

    #endregion
}
