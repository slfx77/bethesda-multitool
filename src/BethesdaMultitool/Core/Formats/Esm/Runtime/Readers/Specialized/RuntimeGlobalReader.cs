using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESGlobal structs (FormType 0x06).
///     Reads cType (value type char) and fValue (float) via the PDB layout.
/// </summary>
internal sealed class RuntimeGlobalReader(RuntimeMemoryContext context)
{
    private const byte GlobFormType = 0x06;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime global-variable record for the given DMP entry, or null if it can't be read.</summary>
    public GlobalRecord? ReadRuntimeGlobal(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != GlobFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, GlobFormType);
        if (view == null)
        {
            return null;
        }

        var valueType = (char)view.Byte("cType", "TESGlobal");
        if (valueType != 's' && valueType != 'l' && valueType != 'f')
        {
            valueType = 'f'; // default to float if unknown
        }

        var rawValue = view.Float("fValue", "TESGlobal");
        var value = RuntimeMemoryContext.IsNormalFloat(rawValue) ? rawValue : 0f;

        return new GlobalRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ValueType = valueType,
            Value = value,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
