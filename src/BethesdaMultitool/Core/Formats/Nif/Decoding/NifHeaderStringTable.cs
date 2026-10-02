using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     The header string table (since 20.1.0.1) re-read as raw bytes. NifParser decodes it with
///     <c>Encoding.ASCII</c> (Parser/NifParser.cs:685), which turns every byte at or above 0x80 into '?'; this table
///     keeps the bytes and offers Latin-1 text, which maps every byte to one character and loses nothing.
/// </summary>
internal sealed class NifHeaderStringTable
{
    private readonly byte[][] _entries;

    /// <summary>Creates a table over the raw entries.</summary>
    /// <param name="offset">The absolute file offset of the table's Num Strings field.</param>
    /// <param name="maxStringLength">The header's declared Max String Length.</param>
    /// <param name="entries">The entries' bytes, in index order.</param>
    public NifHeaderStringTable(int offset, uint maxStringLength, byte[][] entries)
    {
        Offset = offset;
        MaxStringLength = maxStringLength;
        _entries = entries;
    }

    /// <summary>The absolute file offset of the Num Strings field.</summary>
    public int Offset { get; }

    /// <summary>The header's declared Max String Length (not enforced; kept for native state).</summary>
    public uint MaxStringLength { get; }

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Length;

    /// <summary>One entry's bytes exactly as stored.</summary>
    public ReadOnlyMemory<byte> GetRawBytes(int index)
    {
        return _entries[index];
    }

    /// <summary>One entry as Latin-1 text.</summary>
    public string GetText(int index)
    {
        return Encoding.Latin1.GetString(_entries[index]);
    }

    /// <summary>True when an entry contains a byte at or above 0x80 (NifParser shows those as '?').</summary>
    public bool HasNonAsciiBytes(int index)
    {
        return Array.Exists(_entries[index], b => b >= 0x80);
    }

    /// <summary>
    ///     The table's own array for one entry, shared with the immutable <see cref="NifStringValue" />s that resolve to
    ///     it (they expose it only as read-only memory). Callers must not write to it.
    /// </summary>
    internal byte[] SharedEntry(int index)
    {
        return _entries[index];
    }
}
