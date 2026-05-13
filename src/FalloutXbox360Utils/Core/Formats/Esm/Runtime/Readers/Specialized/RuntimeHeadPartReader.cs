using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSHeadPart (HDPT, 96 bytes, FormType 0x09).
///     Reads FullName, model path, and the BGSHeadPart-specific flags byte at +84.
/// </summary>
internal sealed class RuntimeHeadPartReader(RuntimeMemoryContext context)
{
    public HeadPartRecord? ReadRuntimeHeadPart(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != HdptFormType)
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
        var flags = buffer[FlagsOffset];

        return new HeadPartRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte HdptFormType = 0x09;
    private const int StructSize = 96;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int ModelOffset = 56;
    private const int FlagsOffset = 84;

    #endregion
}
