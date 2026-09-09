using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>One member of a <c>.grp</c>: a byte range, plus the 4-byte tag its payload opens with.</summary>
/// <param name="Index">Position in the directory — the only stable identity an entry has.</param>
/// <param name="Offset">Byte offset of the payload.</param>
/// <param name="Size">Payload length.</param>
/// <param name="Tag">The payload's own leading four bytes, printable ones as text.</param>
internal readonly record struct VanBurenGrpEntry(int Index, long Offset, int Size, string Tag)
{
    /// <summary>
    ///     A synthetic name, since the container stores none: the index plus the payload's tag, so a
    ///     listing is still readable and an extraction still lands in sensibly-named files.
    /// </summary>
    public string Name => $"{Index:D5}.{FileExtension}";

    /// <summary>The tag reduced to something usable as a file extension.</summary>
    public string FileExtension
    {
        get
        {
            var cleaned = new string([.. Tag.Where(char.IsLetterOrDigit)]);
            return cleaned.Length > 0 ? cleaned.ToUpperInvariant() : "BIN";
        }
    }
}

/// <summary>
///     The <c>.grp</c> container from the cancelled Fallout 3 "Van Buren" prototype (Dec 9 2003).
///     Original RE 2026-09-06 — the only reference (<c>kran27/VanBurenTools</c>) is GPL, so nothing
///     is ported and the layout is established by exact arithmetic instead.
///     <para>
///         The header is three little-endian dwords — a constant <c>2</c>, a constant <c>1</c>, and
///         the ENTRY COUNT — followed by that many <c>(offset, size)</c> pairs. The six empty
///         archives the build ships (<c>Templates</c>, <c>_CHM</c>, <c>_Dev</c>, <c>_ESF</c>,
///         <c>_MSC</c>, <c>_RLZ</c>) are exactly 12 bytes, which is what pins the header size with
///         no inference at all.
///     </para>
///     <para>
///         ⚑ <b>Payloads TILE.</b> The first entry begins exactly at <c>12 + 8 * count</c> — where
///         the directory ends — each subsequent entry begins where the previous one ended, and the
///         last ends exactly at EOF. That holds on <b>24/24</b> archives covering <b>7,044</b>
///         entries, so parsing is the proof and a malformed file cannot pass.
///     </para>
///     <para>
///         ⚠ <b>Entries carry NO names</b> — only an offset and a size. What identifies a payload is
///         its own leading 4-byte tag, the same self-describing habit Fallout Tactics has. Retail
///         census: <c>B3D </c> 3,915 (meshes), <c>EEN2</c> 285 (every one of the typed
///         <c>_AMO/_ARM/_CON/_CRT/_DOR/_ITM/_USE/_WEA</c> entity groups), <c>VEG </c> 119,
///         <c>RIFF</c> 113 (so its audio is plain WAV), <c>[ANI</c> 113, <c>8TRE</c> 39,
///         <c>EMAP</c> 38, <c>GUI </c> 25.
///     </para>
/// </summary>
internal sealed class VanBurenGrpArchive
{
    /// <summary>Bytes of header before the entry table.</summary>
    public const int HeaderLength = 12;

    /// <summary>Bytes per directory entry: an offset and a size.</summary>
    public const int EntryLength = 8;

    /// <summary>The first header dword, constant across every retail archive.</summary>
    public const uint Magic = 2;

    /// <summary>The second header dword, constant across every retail archive.</summary>
    public const uint Version = 1;

    private VanBurenGrpArchive(string name, IReadOnlyList<VanBurenGrpEntry> entries)
    {
        Name = name;
        Entries = entries;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The members, in directory order.</summary>
    public IReadOnlyList<VanBurenGrpEntry> Entries { get; }

    /// <summary>
    ///     Content probe: the two constant dwords plus the full tiling walk. There is no magic
    ///     string, so the arithmetic IS the probe — a count that does not tile the file exactly
    ///     rejects it.
    /// </summary>
    public static bool IsGrpArchive(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>
    ///     Probes a file without reading its payloads. The largest archive in the build is 232 MB,
    ///     so the probe reads only the header and the directory and checks the tiling against the
    ///     file LENGTH — the same arithmetic the full parse does, at a fraction of the I/O.
    /// </summary>
    public static bool TryProbe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length < HeaderLength)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderLength];
            if (stream.ReadAtLeast(header, HeaderLength, false) < HeaderLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != Version)
            {
                return false;
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (count > int.MaxValue / EntryLength)
            {
                return false;
            }

            var directoryEnd = HeaderLength + (long)count * EntryLength;
            if (directoryEnd > length)
            {
                return false;
            }

            var directory = new byte[count * EntryLength];
            if (stream.ReadAtLeast(directory, directory.Length, false) < directory.Length)
            {
                return false;
            }

            var cursor = directoryEnd;
            for (var i = 0; i < count; i++)
            {
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * EntryLength));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * EntryLength + 4));
                if (offset != cursor || cursor + size > length)
                {
                    return false;
                }

                cursor = offset + (long)size;
            }

            return cursor == length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses the directory, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenGrpArchive Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var archive, out var error))
        {
            throw new InvalidDataException(error);
        }

        return archive;
    }

    /// <summary>Parses the directory, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes,
        string name,
        out VanBurenGrpArchive archive,
        out string error)
    {
        archive = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (magic != Magic || version != Version)
        {
            error = $"{name}: header dwords {magic}/{version} are not the {Magic}/{Version} every archive carries.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (count > int.MaxValue / EntryLength)
        {
            error = $"{name}: {count} entries cannot be addressed.";
            return false;
        }

        var directoryEnd = HeaderLength + (int)count * EntryLength;
        if (directoryEnd > bytes.Length)
        {
            error = $"{name}: {count} entries need {directoryEnd} bytes of {bytes.Length}.";
            return false;
        }

        var entries = new VanBurenGrpEntry[count];
        var cursor = directoryEnd;
        for (var i = 0; i < count; i++)
        {
            var at = HeaderLength + i * EntryLength;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);

            // The tiling gate: an entry must begin exactly where the previous one ended.
            if (offset != cursor || size > int.MaxValue || offset + (long)size > bytes.Length)
            {
                error = $"{name}: entry {i} starts at {offset} rather than {cursor}, or runs past the file.";
                return false;
            }

            entries[i] = new VanBurenGrpEntry(i, offset, (int)size,
                ReadTag(bytes.Slice((int)offset, (int)Math.Min(size, 4))));
            cursor = (int)(offset + size);
        }

        if (cursor != bytes.Length)
        {
            error = $"{name}: entries end at {cursor} of {bytes.Length} bytes.";
            return false;
        }

        archive = new VanBurenGrpArchive(name, entries);
        error = string.Empty;
        return true;
    }

    /// <summary>The payload's leading four bytes as text, non-printable ones as dots.</summary>
    private static string ReadTag(ReadOnlySpan<byte> head)
    {
        var chars = new char[head.Length];
        for (var i = 0; i < head.Length; i++)
        {
            chars[i] = head[i] is >= 32 and < 127 ? (char)head[i] : '.';
        }

        return new string(chars).TrimEnd();
    }

    /// <summary>Reads one entry's payload.</summary>
    public static byte[] Read(ReadOnlySpan<byte> bytes, VanBurenGrpEntry entry)
    {
        return bytes.Slice((int)entry.Offset, entry.Size).ToArray();
    }
}
