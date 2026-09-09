using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Steam;

/// <summary>
///     The <c>.sim</c> file table of a Steam retail disc ("Steam Installation Manifest").
///     <para>
///         A Steam retail box is not a standalone release: the disc ships <c>SteamService.exe</c>,
///         an install MSI, a <c>.sis</c> install script naming the appid and its depots, this
///         manifest, and the payload split across <c>&lt;name&gt;_&lt;disk&gt;_&lt;part&gt;.sid</c>
///         files. The manifest is PLAINTEXT even though the payload is encrypted, so a listing
///         needs no key at all — only <see cref="SteamInstallerArchive.Extract" /> does.
///     </para>
///     <para>
///         Layout, measured on the Fallout: New Vegas disc (2010-09-16 press, 431 files):
///         <code>
///         +0   u32  magic 0x3FD04C1F
///         +4   u32  1
///         +8   u32  1
///         +12  u32  string table size
///         +16  u16  0
///         +24  ..   string table, NUL-terminated entries, running to the record table
///                   record table: 32 bytes x N, ENDING EXACTLY AT EOF
///         </code>
///     </para>
///     <para>
///         ⚠ The record count is NOT stored. It is derived: the table begins at
///         <c>(stringTableSize + 24)</c> and runs to EOF, so <c>N = (length - tableStart) / 32</c>.
///         On the retail disc that is <c>12026 + 24 = 12050</c> and <c>(25842 - 12050) / 32 = 431</c>
///         exactly. Both the exact division and the exact EOF landing are ENFORCED — a manifest that
///         does not tile is rejected rather than read past its end.
///     </para>
///     <para>
///         ⚠⚠ String offsets are relative to <c>+16</c>, NOT to the file start and NOT to the table
///         start. Reading them as absolute yields plausible-looking but WRONG names
///         (<c>'reditsWacky.txt'</c>, <c>'eo'</c>) — wrong in a way that still parses, which is why
///         this is stated rather than left to the reader.
///     </para>
/// </summary>
internal sealed class SteamInstallerManifest
{
    /// <summary>Magic at <c>+0</c>.</summary>
    internal const uint Magic = 0x3FD04C1F;

    private const int RecordSize = 32;
    private const int StringBase = 16;
    private const int StringTableStart = 24;

    private SteamInstallerManifest(IReadOnlyList<SteamInstallerFile> files)
    {
        Files = files;
    }

    /// <summary>Every declared file, in manifest order.</summary>
    internal IReadOnlyList<SteamInstallerFile> Files { get; }

    /// <summary>The distinct depot ids the manifest references, ascending.</summary>
    internal IReadOnlyList<uint> Depots =>
        [.. Files.Select(static f => f.DepotId).Distinct().Order()];

    /// <summary>Cheap content probe: the magic alone, without reading the table.</summary>
    internal static bool HasMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, false) == 4 &&
                   BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Parses a <c>.sim</c>. Throws <see cref="InvalidDataException" /> when it does not tile.</summary>
    internal static SteamInstallerManifest Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Parses an in-memory <c>.sim</c>; <paramref name="origin" /> only names it in errors.</summary>
    internal static SteamInstallerManifest Parse(ReadOnlySpan<byte> data, string origin)
    {
        if (data.Length < StringTableStart)
        {
            throw new InvalidDataException($"'{origin}' is too short to be a .sim manifest.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException($"'{origin}' does not carry the .sim magic 0x{Magic:X8}.");
        }

        var stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var tableStart = checked((long)stringTableSize + StringTableStart);
        if (tableStart < StringTableStart || tableStart > data.Length)
        {
            throw new InvalidDataException(
                $"'{origin}' declares a {stringTableSize}-byte string table that does not fit in {data.Length} bytes.");
        }

        var tableBytes = data.Length - tableStart;
        if (tableBytes % RecordSize != 0)
        {
            throw new InvalidDataException(
                $"'{origin}' record table is {tableBytes} bytes, not a multiple of {RecordSize} — the table must end exactly at EOF.");
        }

        var count = (int)(tableBytes / RecordSize);
        var files = new List<SteamInstallerFile>(count);
        for (var i = 0; i < count; i++)
        {
            var record = data.Slice((int)tableStart + i * RecordSize, RecordSize);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var folderOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var depot = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            var location = BinaryPrimitives.ReadUInt32LittleEndian(record[28..]);

            var name = ReadString(data, nameOffset, origin);
            var folder = ReadString(data, folderOffset, origin);
            var full = folder.Length == 0 ? name : folder + "/" + name;

            files.Add(new SteamInstallerFile(
                NormalizePath(full),
                depot,
                offset,
                size,
                (int)(location & 0xFF),
                (int)((location >> 8) & 0xFF)));
        }

        return new SteamInstallerManifest(files);
    }

    /// <summary>
    ///     Reads a NUL-terminated entry. ⚠ <paramref name="offset" /> is relative to
    ///     <see cref="StringBase" />, not to the file start.
    /// </summary>
    private static string ReadString(ReadOnlySpan<byte> data, uint offset, string origin)
    {
        var start = checked((long)offset + StringBase);
        if (start < 0 || start >= data.Length)
        {
            throw new InvalidDataException($"'{origin}' string offset {offset} falls outside the file.");
        }

        var slice = data[(int)start..];
        var end = slice.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException($"'{origin}' string at {offset} is not NUL-terminated.");
        }

        // Steam wrote these as single-byte text; Latin-1 round-trips every retail byte.
        return Encoding.Latin1.GetString(slice[..end]);
    }

    private static string NormalizePath(string value)
    {
        var separator = (char)92; // backslash, as the manifest stores it
        return value.Replace(separator, '/').TrimStart('/');
    }
}

/// <summary>
///     One file declared by a <see cref="SteamInstallerManifest" />.
/// </summary>
/// <param name="Path">Virtual path, forward-slashed, relative to the install root.</param>
/// <param name="DepotId">Owning depot — selects the decryption key.</param>
/// <param name="Offset">
///     Byte offset of the file's first block WITHIN ITS OWN <c>.sid</c> part. ⚠ Per part, never
///     global: offsets top out near 1 GB while the payload runs to 6.7 GB, so without
///     <see cref="PartIndex" /> the two cannot be reconciled.
/// </param>
/// <param name="Size">Uncompressed size, and the extraction stop condition.</param>
/// <param name="DiskNumber">Disc this file lives on (1 for a single-disc release).</param>
/// <param name="PartIndex">Which <c>.sid</c> part the file starts in.</param>
internal sealed record SteamInstallerFile(
    string Path,
    uint DepotId,
    long Offset,
    long Size,
    int DiskNumber,
    int PartIndex);
