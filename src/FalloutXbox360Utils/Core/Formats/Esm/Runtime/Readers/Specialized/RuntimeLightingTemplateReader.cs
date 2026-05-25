using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSLightingTemplate (LGTM, 84 bytes, FormType 0x65).
///     Reads the INTERIOR_DATA struct at PDB <c>Data</c> (+40, 44B; first 40B match
///     the ESM DATA subrecord schema) and produces the same field dictionary the
///     ESM handler populates.
/// </summary>
internal sealed class RuntimeLightingTemplateReader(RuntimeMemoryContext context)
{
    private const byte LgtmFormType = 0x65;
    // INTERIOR_DATA is 44 bytes in the runtime struct, but only the first 40 bytes
    // match the ESM DATA subrecord schema (the last 4 bytes are runtime padding/extra).
    private const int EsmDataSize = 40;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public LightingTemplateRecord? ReadRuntimeLightingTemplate(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != LgtmFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, LgtmFormType);
        if (view == null)
        {
            return null;
        }

        Dictionary<string, object?>? lightingData = null;
        if (view.Offset("Data", "BGSLightingTemplate") is { } dataOff)
        {
            var dataBytes = new byte[EsmDataSize];
            Array.Copy(view.Buffer, dataOff, dataBytes, 0, EsmDataSize);
            lightingData = SubrecordSchemaView.TryRead("DATA", "LGTM", dataBytes, bigEndian: true)?.Raw;
        }

        return new LightingTemplateRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            LightingData = lightingData,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
