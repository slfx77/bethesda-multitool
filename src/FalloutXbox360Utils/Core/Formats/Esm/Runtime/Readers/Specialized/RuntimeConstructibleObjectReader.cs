using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSConstructibleObject (COBJ, 196 bytes, FormType 0x32).
///     Reads the CreatedItem FormID (pCreatedItem pointer at +192) to confirm the
///     ESM CNAM subrecord value. pRequiredItems (+188, BGSListForm) is the
///     materialized inline ingredient list — typically anonymous (FormID 0) so we
///     don't expose it.
/// </summary>
internal sealed class RuntimeConstructibleObjectReader(RuntimeMemoryContext context)
{
    public ConstructibleObjectRecord? ReadRuntimeConstructibleObject(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != CobjFormType)
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

        var createdItemFormId = context.FollowPointerToFormId(buffer, CreatedItemPointerOffset);

        return new ConstructibleObjectRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CreatedItemFormId = createdItemFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte CobjFormType = 0x32;
    private const int StructSize = 196;
    private const int FormIdOffset = 12;
    private const int CreatedItemPointerOffset = 192;

    #endregion
}
