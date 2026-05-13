using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESLoadScreenType (LSCT, 128 bytes, FormType 0x6E).
///     Reads the 88-byte LoadScreenType_Data block at +40 via the shared
///     SubrecordDataReader schema (DATA/LSCT).
/// </summary>
internal sealed class RuntimeLoadScreenTypeReader(RuntimeMemoryContext context)
{
    public LoadScreenTypeRecord? ReadRuntimeLoadScreenType(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != LsctFormType)
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

        var dataBytes = new byte[DataSize];
        Array.Copy(buffer, DataOffset, dataBytes, 0, DataSize);
        var layoutData = SubrecordDataReader.ReadFields("DATA", "LSCT", dataBytes, true);

        return new LoadScreenTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            LayoutData = layoutData.Count > 0 ? layoutData : null,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte LsctFormType = 0x6E;
    private const int StructSize = 128;
    private const int FormIdOffset = 12;
    private const int DataOffset = 40;
    private const int DataSize = 88;

    #endregion
}
