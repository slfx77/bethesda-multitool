using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

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
    ///     guards that <see cref="ReadStruct(RuntimeEditorIdEntry)" /> applies (no PDB layout, buffer read failure,
    ///     FormType byte mismatch, FormID mismatch).
    /// </summary>
    internal PdbStructView? OpenStructView(RuntimeEditorIdEntry entry)
    {
        return OpenStructView(entry, entry.FormType);
    }

    /// <summary>
    ///     Open a struct view using the layout for <paramref name="pdbFormType" /> instead of
    ///     the entry's runtime FormType byte. Used by readers whose runtime FormType byte
    ///     differs from the PDB-declared key due to per-build FormType drift (e.g. LAND lives
    ///     at runtime byte 0x43 in Fallout_Release_Beta.xex.dmp but PDB key is 0x44).
    /// </summary>
    internal PdbStructView? OpenStructView(RuntimeEditorIdEntry entry, byte pdbFormType)
    {
        var data = ReadStruct(entry, pdbFormType);
        return data is null
            ? null
            : new PdbStructView(this, data.Value.Layout, data.Value.Buffer, data.Value.FileOffset, entry);
    }

    internal (PdbTypeLayout Layout, byte[] Buffer, long FileOffset)? ReadStruct(RuntimeEditorIdEntry entry)
    {
        return ReadStruct(entry, entry.FormType);
    }

    internal (PdbTypeLayout Layout, byte[] Buffer, long FileOffset)? ReadStruct(
        RuntimeEditorIdEntry entry, byte pdbFormType)
    {
        if (!entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var layout = PdbStructLayouts.Get(pdbFormType);
        if (layout == null)
        {
            return null;
        }

        // Runtime form maps store TESForm*, which can point inside the complete object. PDB field
        // offsets are complete-object-relative, so recover the object base before reading or
        // validating any field. Doing the subtraction in VA space is essential: VA-contiguous
        // minidump regions need not be adjacent in the dump file.
        var tesFormVa = entry.TesFormPointer is { } pointer && pointer != 0
            ? pointer
            : _context.MinidumpInfo.FileOffsetToVirtualAddress(entry.TesFormOffset.Value);
        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        var objectFileOffset = entry.TesFormOffset.Value;
        byte[]? buffer;
        if (tesFormVa.HasValue)
        {
            if (tesFormVa.Value < long.MinValue + interiorOffset)
            {
                return null;
            }

            var objectVa = tesFormVa.Value - interiorOffset;
            var mappedOffset = _context.MinidumpInfo.VirtualAddressToFileOffset(objectVa);
            if (!mappedOffset.HasValue)
            {
                return null;
            }

            objectFileOffset = mappedOffset.Value;
            buffer = _context.ReadBytesAtVa(objectVa, layout.StructSize);
        }
        else
        {
            // Lightweight synthetic callers may not provide a minidump region map. Preserve the
            // flat-read fallback for them, but still honor the TESForm-interior contract.
            if (objectFileOffset < interiorOffset)
            {
                return null;
            }

            objectFileOffset -= interiorOffset;
            buffer = _context.ReadBytes(objectFileOffset, layout.StructSize);
        }

        if (buffer == null)
        {
            return null;
        }

        // The buffer now starts at the complete-object base, so the flattened PDB offsets are the
        // authoritative validation positions for both TESForm-first and multi-inheritance layouts.
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

        return (layout, buffer, objectFileOffset);
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

    internal string? ReadBsString(
        byte[] structData,
        long fileOffset,
        PdbTypeLayout layout,
        string name,
        string? owner,
        RuntimeEditorIdEntry entry)
    {
        return ReadBsStringAtOffset(
            structData,
            fileOffset,
            name,
            FindFieldOffset(layout, name, owner),
            entry);
    }

    /// <summary>
    ///     Pre-computed-offset overload for a struct buffer already read through the VA-safe
    ///     complete-object path. Used by <see cref="PdbStructView" /> when a
    ///     <see cref="PdbStructView.WithShift(int,int,int)" /> band has adjusted the field offset.
    ///     The pointed-to payload remains VA-validated by the context.
    /// </summary>
    internal string? ReadBsStringAtOffset(byte[] structData, string name, int? fieldOffset)
    {
        if (!fieldOffset.HasValue)
        {
            return null;
        }

        var result = _context.ReadBSStringTDiag(structData, fieldOffset.Value, out var failure);
        BSStringDiagnostics.Record(name, failure);
        return result;
    }

    internal string? ReadBsStringAtOffset(
        byte[] structData,
        long fileOffset,
        string name,
        int? fieldOffset,
        RuntimeEditorIdEntry entry)
    {
        if (!fieldOffset.HasValue)
        {
            return null;
        }

        var result = _context.ReadBSStringTDiag(structData, fieldOffset.Value, out var failure,
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
        foreach (var itemPtr in _context.WalkInlineBSSimpleListItemPointers(
                     structBuffer, listHeadOffset, maxItems))
        {
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
        foreach (var itemPtr in _context.WalkInlineBSSimpleListItemPointers(
                     structBuffer, listHeadOffset, maxItems))
        {
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
