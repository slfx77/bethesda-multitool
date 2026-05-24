using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

/// <summary>
///     Shared helpers for PDB-backed runtime readers.
///     Resolves top-level field offsets from <see cref="PdbStructLayouts" /> and walks common
///     inline BSSimpleList patterns used by runtime TESForm structs.
/// </summary>
internal sealed class RuntimePdbFieldAccessor(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    /// <summary>
    ///     Opens a typed view over the entry's runtime struct. Returns null on the same
    ///     guards that <see cref="ReadStruct" /> applies (no PDB layout, buffer read failure,
    ///     FormType byte mismatch, FormID mismatch).
    /// </summary>
    internal PdbStructView? OpenStructView(RuntimeEditorIdEntry entry)
    {
        var data = ReadStruct(entry);
        return data is null
            ? null
            : new PdbStructView(this, _context, data.Value.Layout, data.Value.Buffer, data.Value.FileOffset, entry);
    }

    internal (PdbTypeLayout Layout, byte[] Buffer, long FileOffset)? ReadStruct(RuntimeEditorIdEntry entry)
    {
        if (!entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var layout = PdbStructLayouts.Get(entry.FormType);
        if (layout == null)
        {
            return null;
        }

        var buffer = _context.ReadBytes(entry.TesFormOffset.Value, layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        // Resolve cFormType / iFormID offsets from the PDB layout. Most classes have
        // TESForm at offset 0 (cFormType @ +4, iFormID @ +12) but multi-inheritance
        // classes like TESFlora (cFormType @ +16) and BGSMovableStatic (cFormType @ +24)
        // put base-class members before TESForm. Looking up the PDB-resolved offsets
        // makes the validation work for both layouts.
        var cFormTypeOff = FindFieldOffset(layout, "cFormType", "TESForm");
        var iFormIdOff = FindFieldOffset(layout, "iFormID", "TESForm");
        if (cFormTypeOff is not { } ftOff || iFormIdOff is not { } fidOff
            || ftOff + 1 > buffer.Length || fidOff + 4 > buffer.Length)
        {
            return null;
        }

        if (buffer[ftOff] != entry.FormType &&
            buffer[ftOff] != (entry.OriginalFormType ?? entry.FormType))
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, fidOff);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        return (layout, buffer, entry.TesFormOffset.Value);
    }

    internal static int? FindFieldOffset(PdbTypeLayout layout, string name, string? owner = null)
    {
        var field = layout.Fields.FirstOrDefault(f => f.Name == name && (owner == null || f.Owner == owner));
        return field?.Offset;
    }

    internal static ObjectBounds? ReadBounds(byte[] buffer, PdbTypeLayout layout)
    {
        var boundsOffset = FindFieldOffset(layout, "BoundData", "TESBoundObject");
        if (!boundsOffset.HasValue || boundsOffset.Value + 12 > buffer.Length)
        {
            return null;
        }

        var bounds = RecordParserContext.ReadObjectBounds(buffer.AsSpan(boundsOffset.Value, 12), true);
        return bounds is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 } ? null : bounds;
    }

    internal string? ReadBsString(long fileOffset, PdbTypeLayout layout, string name, string? owner = null)
    {
        return ReadBsStringAtOffset(fileOffset, name, FindFieldOffset(layout, name, owner));
    }

    internal string? ReadBsString(long fileOffset, PdbTypeLayout layout, string name, string? owner,
        RuntimeEditorIdEntry entry)
    {
        return ReadBsStringAtOffset(fileOffset, name, FindFieldOffset(layout, name, owner), entry);
    }

    /// <summary>
    ///     Pre-computed-offset overload — used by <see cref="PdbStructView" /> when a
    ///     <see cref="PdbStructView.WithShift" /> band has adjusted the field offset.
    /// </summary>
    internal string? ReadBsStringAtOffset(long fileOffset, string name, int? fieldOffset)
    {
        if (!fieldOffset.HasValue)
        {
            return null;
        }

        var result = _context.ReadBSStringTDiag(fileOffset, fieldOffset.Value, out var failure);
        BSStringDiagnostics.Record(name, failure);
        return result;
    }

    internal string? ReadBsStringAtOffset(long fileOffset, string name, int? fieldOffset,
        RuntimeEditorIdEntry entry)
    {
        if (!fieldOffset.HasValue)
        {
            return null;
        }

        var result = _context.ReadBSStringTDiag(fileOffset, fieldOffset.Value, out var failure,
            out var ptr, out var len, out var hex, out var partial);
        BSStringDiagnostics.RecordWithSample(name, failure,
            new BSStringDiagnostics.DiagSample(entry.FormId, entry.EditorId, entry.FormType,
                fileOffset, fieldOffset.Value, ptr, len, hex, partial));
        return result;
    }

    internal uint? ReadFormIdPointer(
        byte[] buffer,
        PdbTypeLayout layout,
        string name,
        string? owner = null,
        byte? expectedFormType = null)
    {
        var fieldOffset = FindFieldOffset(layout, name, owner);
        return fieldOffset.HasValue
            ? ReadPointerToFormId(buffer, fieldOffset.Value, expectedFormType)
            : null;
    }

    internal uint? ReadPointerToFormId(byte[] buffer, int fieldOffset, byte? expectedFormType = null)
    {
        if (fieldOffset + 4 > buffer.Length)
        {
            return null;
        }

        return expectedFormType.HasValue
            ? _context.FollowPointerToFormId(buffer, fieldOffset, expectedFormType.Value)
            : _context.FollowPointerToFormId(buffer, fieldOffset);
    }

    internal List<uint> ReadFormIdSimpleList(
        byte[] structBuffer,
        int listHeadOffset,
        byte? expectedFormType = null,
        int maxItems = RuntimeMemoryContext.MaxListItems)
    {
        var formIds = new List<uint>();
        if (listHeadOffset + 8 > structBuffer.Length)
        {
            return formIds;
        }

        var itemPtr = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset + 4);

        AddPointerFormId(formIds, itemPtr, expectedFormType);

        var visited = new HashSet<uint>();
        while (nextPtr != 0 && formIds.Count < maxItems && _context.IsValidPointer(nextPtr) && visited.Add(nextPtr))
        {
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            itemPtr = BinaryUtils.ReadUInt32BE(nodeBuffer);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);
            AddPointerFormId(formIds, itemPtr, expectedFormType);
        }

        return formIds;
    }

    internal List<T> ReadSimpleList<T>(
        byte[] structBuffer,
        int listHeadOffset,
        Func<uint, T?> itemReader,
        int maxItems = RuntimeMemoryContext.MaxListItems)
        where T : class
    {
        var results = new List<T>();
        if (listHeadOffset + 8 > structBuffer.Length)
        {
            return results;
        }

        var itemPtr = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset + 4);

        AddListItem(results, itemPtr, itemReader);

        var visited = new HashSet<uint>();
        while (nextPtr != 0 && results.Count < maxItems && _context.IsValidPointer(nextPtr) && visited.Add(nextPtr))
        {
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            itemPtr = BinaryUtils.ReadUInt32BE(nodeBuffer);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);
            AddListItem(results, itemPtr, itemReader);
        }

        return results;
    }

    internal static float ReadFloat(byte[] buffer, int offset)
    {
        return BinaryUtils.ReadFloatBE(buffer, offset);
    }

    internal static int ReadInt32(byte[] buffer, int offset)
    {
        return RuntimeMemoryContext.ReadInt32BE(buffer, offset);
    }

    internal static uint ReadUInt32(byte[] buffer, int offset)
    {
        return BinaryUtils.ReadUInt32BE(buffer, offset);
    }

    internal static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return BinaryUtils.ReadUInt16BE(buffer, offset);
    }

    private static void AddListItem<T>(List<T> results, uint itemPtr, Func<uint, T?> itemReader)
        where T : class
    {
        if (itemPtr == 0)
        {
            return;
        }

        var item = itemReader(itemPtr);
        if (item != null)
        {
            results.Add(item);
        }
    }

    private void AddPointerFormId(List<uint> results, uint itemPtr, byte? expectedFormType)
    {
        if (itemPtr == 0)
        {
            return;
        }

        var formId = expectedFormType.HasValue
            ? _context.FollowPointerVaToFormId(itemPtr, expectedFormType.Value)
            : _context.FollowPointerVaToFormId(itemPtr);
        if (formId is > 0)
        {
            results.Add(formId.Value);
        }
    }
}
