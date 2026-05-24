using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESLoadScreenType (LSCT, 128 bytes, FormType 0x6E).
///     Reads the 88-byte LoadScreenType_Data block at PDB <c>data</c> via the shared
///     SubrecordSchemaView schema (DATA/LSCT).
/// </summary>
internal sealed class RuntimeLoadScreenTypeReader(RuntimeMemoryContext context)
{
    private const byte LsctFormType = 0x6E;
    private const int DataSize = 88;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public LoadScreenTypeRecord? ReadRuntimeLoadScreenType(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != LsctFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        Dictionary<string, object?>? layoutData = null;
        if (view.Offset("data", "TESLoadScreenType") is { } dataOff)
        {
            var dataBytes = new byte[DataSize];
            Array.Copy(view.Buffer, dataOff, dataBytes, 0, DataSize);
            layoutData = SubrecordSchemaView.TryRead("DATA", "LSCT", dataBytes, bigEndian: true)?.Raw;
        }

        return new LoadScreenTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            LayoutData = layoutData,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
