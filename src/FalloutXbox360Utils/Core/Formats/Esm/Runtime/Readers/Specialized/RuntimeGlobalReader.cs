using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESGlobal structs (FormType 0x06, 48 bytes).
///     Reads cType (value type char) and fValue (float).
/// </summary>
internal sealed class RuntimeGlobalReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeGlobalReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public GlobalRecord? ReadRuntimeGlobal(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != GlobFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
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

        var valueType = (char)buffer[TypeOffset];
        if (valueType != 's' && valueType != 'l' && valueType != 'f')
        {
            valueType = 'f'; // default to float if unknown
        }

        var value = BinaryUtils.ReadFloatBE(buffer, ValueOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(value))
        {
            value = 0f;
        }

        return new GlobalRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ValueType = valueType,
            Value = value,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte GlobFormType = 0x06;
    private const int StructSize = 48;
    private const int FormIdOffset = 12;
    private const int TypeOffset = 40; // cType (int8 — 's', 'l', or 'f')
    private const int ValueOffset = 44; // fValue (float32)

    #endregion
}
