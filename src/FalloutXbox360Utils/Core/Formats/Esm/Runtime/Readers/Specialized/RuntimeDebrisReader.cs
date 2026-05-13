using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSDebris (DEBR, 52 bytes, FormType 0x52).
///     Walks the DataList BSSimpleList at +44 to count debris variants.
///     Doesn't unpack the per-variant BGSDebrisData (opaque struct).
/// </summary>
internal sealed class RuntimeDebrisReader(RuntimeMemoryContext context)
{
    public DebrisRecord? ReadRuntimeDebris(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != DebrFormType)
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

        var variantCount = 0;
        foreach (var _ in context.WalkInlineBSSimpleListItemPointers(buffer, DataListOffset))
        {
            variantCount++;
        }

        return new DebrisRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            VariantCount = variantCount,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte DebrFormType = 0x52;
    private const int StructSize = 52;
    private const int FormIdOffset = 12;
    private const int DataListOffset = 44;

    #endregion
}
