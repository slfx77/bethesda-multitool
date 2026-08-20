using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Reads BSStringT&lt;char&gt; fields from runtime objects in Xbox 360 memory dumps.
///     Shared by <see cref="EsmEditorIdExtractor" /> and <see cref="EditorIdLookupTables" />.
/// </summary>
internal static class EsmEditorIdStringReader
{
    /// <summary>
    ///     Read a BSStringT&lt;char&gt; field at a complete-object-relative offset. Both the
    ///     eight-byte header and pointed-to payload are read in VA space, so VA-contiguous
    ///     regions can be stitched regardless of their physical dump-file placement and
    ///     capture gaps fail closed.
    ///     BSStringT layout (8 bytes, big-endian on Xbox 360):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    internal static ReadResult? ReadBsStringTAtVa(
        RuntimeMemoryContext context,
        long objectVa,
        int fieldOffset)
    {
        if (fieldOffset < 0 || objectVa > long.MaxValue - fieldOffset)
        {
            return null;
        }

        var header = context.ReadBytesAtVa(objectVa + fieldOffset, 8);
        if (header == null)
        {
            return null;
        }

        var text = context.ReadBSStringTDiag(header, 0, out _,
            out var stringVa, out _, out _, out _);
        if (text == null)
        {
            return null;
        }

        var stringFileOffset = context.MinidumpInfo.VirtualAddressToFileOffset(
            Xbox360MemoryUtils.VaToLong(stringVa));
        if (!stringFileOffset.HasValue)
        {
            return null;
        }

        return new ReadResult(text, stringFileOffset.Value);
    }

    /// <summary>
    ///     Resolves an entry's captured TESForm subobject VA, preferring the retained pointer
    ///     and falling back to the file-offset mapping for synthetic or legacy entries.
    /// </summary>
    internal static ReadResult? ReadFromTesFormEntry(
        RuntimeMemoryContext context,
        RuntimeEditorIdEntry entry,
        int tesFormRelativeFieldOffset)
    {
        long? tesFormVa = null;
        if (entry.TesFormPointer is { } pointer && pointer != 0)
        {
            tesFormVa = pointer;
        }
        else if (entry.TesFormOffset is { } fileOffset)
        {
            tesFormVa = context.MinidumpInfo.FileOffsetToVirtualAddress(fileOffset);
        }

        return tesFormVa.HasValue
            ? ReadBsStringTAtVa(context, tesFormVa.Value, tesFormRelativeFieldOffset)
            : null;
    }

    internal readonly record struct ReadResult(string Text, long StringFileOffset);
}
