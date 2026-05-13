using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMenuIcon (MICN, 52 bytes, FormType 0x05).
///     Reads TextureName BSStringT at +44.
/// </summary>
internal sealed class RuntimeMenuIconReader(RuntimeMemoryContext context)
{
    public MenuIconRecord? ReadRuntimeMenuIcon(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != MicnFormType)
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

        var iconPath = context.ReadBsStringT(offset, IconOffset);

        return new MenuIconRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            IconPath = iconPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte MicnFormType = 0x05;
    private const int StructSize = 52;
    private const int FormIdOffset = 12;
    private const int IconOffset = 44;

    #endregion
}
