using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRecipeCategory (RCCT, 56 bytes, FormType 0x6B).
///     Reads the display name (BSStringT at +44) and the 1-byte flags from
///     RECIPE_CATEGORY_DATA at +52.
/// </summary>
internal sealed class RuntimeRecipeCategoryReader(RuntimeMemoryContext context)
{
    public RecipeCategoryRecord? ReadRuntimeRecipeCategory(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RcctFormType)
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
        var flags = buffer[DataOffset];

        return new RecipeCategoryRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte RcctFormType = 0x6B;
    private const int StructSize = 56;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int DataOffset = 52;

    #endregion
}
