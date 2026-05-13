using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSLightingTemplate (LGTM, 84 bytes, FormType 0x65).
///     Reads the INTERIOR_DATA struct at +40 (44B, of which the first 40B match the
///     ESM DATA subrecord schema) and produces the same field dictionary the ESM
///     handler populates.
/// </summary>
internal sealed class RuntimeLightingTemplateReader(RuntimeMemoryContext context)
{
    public LightingTemplateRecord? ReadRuntimeLightingTemplate(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != LgtmFormType)
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

        var dataBytes = new byte[EsmDataSize];
        Array.Copy(buffer, DataOffset, dataBytes, 0, EsmDataSize);
        var lightingData = SubrecordDataReader.ReadFields("DATA", "LGTM", dataBytes, true);

        return new LightingTemplateRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            LightingData = lightingData.Count > 0 ? lightingData : null,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte LgtmFormType = 0x65;
    private const int StructSize = 84;
    private const int FormIdOffset = 12;
    private const int DataOffset = 40;
    private const int EsmDataSize = 40; // First 40B of the 44B INTERIOR_DATA match ESM DATA schema

    #endregion
}
