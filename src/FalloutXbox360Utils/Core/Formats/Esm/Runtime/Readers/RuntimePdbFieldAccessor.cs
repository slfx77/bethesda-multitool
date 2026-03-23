using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Shared helpers for PDB-backed runtime readers.
///     Resolves top-level field offsets from <see cref="PdbStructLayouts" /> and walks common
///     inline BSSimpleList patterns used by runtime TESForm structs.
/// </summary>
internal sealed class RuntimePdbFieldAccessor(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

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

        if (buffer.Length < 16 || buffer[4] != entry.FormType)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        return (layout, buffer, entry.TesFormOffset.Value);
    }

    internal int? FindFieldOffset(PdbTypeLayout layout, string name, string? owner = null)
    {
        var field = layout.Fields.FirstOrDefault(f => f.Name == name && (owner == null || f.Owner == owner));
        return field?.Offset;
    }

    internal ObjectBounds? ReadBounds(byte[] buffer, PdbTypeLayout layout)
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
        var fieldOffset = FindFieldOffset(layout, name, owner);
        if (!fieldOffset.HasValue)
        {
            return null;
        }

        var result = _context.ReadBSStringTDiag(fileOffset, fieldOffset.Value, out var failure);
        BSStringDiagnostics.Record(name, failure);
        return result;
    }

    internal string? ReadBsString(long fileOffset, PdbTypeLayout layout, string name, string? owner,
        RuntimeEditorIdEntry entry)
    {
        var fieldOffset = FindFieldOffset(layout, name, owner);
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
