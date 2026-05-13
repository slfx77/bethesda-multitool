using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSVoiceType (VTYP, 44 bytes, FormType 0x5D).
///     Reads the VOICE_TYPE_DATA byte at +40.
/// </summary>
internal sealed class RuntimeVoiceTypeReader(RuntimeMemoryContext context)
{
    public VoiceTypeRecord? ReadRuntimeVoiceType(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != VtypFormType)
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

        return new VoiceTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Flags = buffer[DataOffset],
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte VtypFormType = 0x5D;
    private const int StructSize = 44;
    private const int FormIdOffset = 12;
    private const int DataOffset = 40;

    #endregion
}
