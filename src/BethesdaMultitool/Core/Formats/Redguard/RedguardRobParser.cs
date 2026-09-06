using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>.ROB</c> per-map object archive: the meshes for one location, concatenated
///     into a single file. Structure ported from RGUnity/redguard-file-exporter (MIT, by UESP's
///     Daveh) — <c>3DFileTest/Common/RedguardRobFile.{h,cpp}</c>, whose <c>Generate3DFile</c>
///     copies each segment's bytes straight out to a <c>.3D</c> file.
///     <para>
///         Chunked, mixed-endian — the plan's "LE values, BE chunk lengths" note is literally
///         true here. A 20-byte prologue holds two chunk headers: <c>"OARC"</c> with a
///         big-endian length of 4 whose little-endian payload is the segment count, then
///         <c>"OARD"</c> with a big-endian length covering every segment that follows. The file
///         ends with a 4-byte <c>"END "</c> terminator, so header + OARD body + 4 accounts for
///         every byte.
///     </para>
///     <para>
///         Each segment is an 80-byte header followed immediately by its payload:
///         <c>OffsetNextSegment</c> (always <c>80 + Size</c>), an 8-byte NUL-padded ASCII name,
///         a type word (0 or 512), fifteen unidentified dwords, and <c>Size</c> as the last one.
///         The reference walks sequentially and never reads <c>OffsetNextSegment</c>; both routes
///         agree on every retail file.
///     </para>
///     <para>
///         Nothing here is compressed. <c>GR_COMP</c> is simply a mesh name — it leads 39 of the
///         41 retail archives, which is why earlier notes mistook it for a compression marker.
///     </para>
///     <para>
///         Verified against all 41 retail archives (2026-09-04): 5,870 segments, every name
///         printable and unique within its archive, 1,203 of them empty, and all 4,667 payloads
///         genuine <c>.3D</c> meshes — 4,450 tagged <c>v2.7</c> and 217 <c>v2.6</c>, every one
///         reproducing its planes' stored normals from <c>OffsetVertexCoors</c>. So a <c>v2.6</c>
///         tag does NOT imply the unsolved <c>.3DC</c> layout; the two are separate things.
///     </para>
/// </summary>
internal static class RedguardRobParser
{
    /// <summary>Bytes of prologue before the first segment header.</summary>
    public const int HeaderLength = 20;

    /// <summary>Bytes in a segment header, before its payload.</summary>
    public const int SegmentHeaderLength = 80;

    /// <summary>Bytes of <c>"END "</c> terminator after the last segment.</summary>
    public const int TerminatorLength = 4;

    /// <summary>Bytes reserved for a segment name, NUL-padded.</summary>
    private const int NameBytes = 8;

    /// <summary>Offset of <c>Size</c> within a segment header — it is the last of the twenty dwords.</summary>
    private const int SizeOffset = 76;

    private static ReadOnlySpan<byte> CountChunkMagic => "OARC"u8;

    private static ReadOnlySpan<byte> DataChunkMagic => "OARD"u8;

    private static ReadOnlySpan<byte> Terminator => "END "u8;

    /// <summary>
    ///     Exact-arithmetic content probe. Both chunk magics must be present, the OARC length must
    ///     be the 4 its single payload dword occupies, every segment must tile forward without
    ///     overrunning, and the walk must end exactly on the OARD chunk's declared length with the
    ///     <c>"END "</c> terminator and nothing else after it.
    /// </summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDirectory(stream) is not null;
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

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static RedguardRobArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryReadDirectory(stream) ??
               throw new InvalidDataException(
                   $"'{Path.GetFileName(path)}' is not a Redguard ROB archive: its segments do not tile the file.");
    }

    private static RedguardRobArchive? TryReadDirectory(FileStream stream)
    {
        var fileLength = stream.Length;
        if (fileLength < HeaderLength + TerminatorLength)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual(CountChunkMagic) || !header[12..16].SequenceEqual(DataChunkMagic))
        {
            return null;
        }

        // The OARC chunk's big-endian length; its body is the one dword that follows.
        if (BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != sizeof(uint))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var dataLength = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        if (count == 0 || HeaderLength + (long)dataLength + TerminatorLength != fileLength)
        {
            return null;
        }

        var entries = new List<RedguardRobEntry>((int)Math.Min(count, 8192));
        Span<byte> segment = stackalloc byte[SegmentHeaderLength];
        long position = HeaderLength;
        var payloadEnd = HeaderLength + (long)dataLength;

        for (uint i = 0; i < count; i++)
        {
            if (position + SegmentHeaderLength > payloadEnd)
            {
                return null;
            }

            stream.Position = position;
            stream.ReadExactly(segment);

            var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(segment);
            var name = ReadName(segment.Slice(4, NameBytes));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(segment[12..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(segment[SizeOffset..]);

            // The forward pointer and the sequential walk must agree — they do on every retail
            // archive, so a disagreement means the walk has desynchronised, not a format variant.
            if (name is null || nextOffset != SegmentHeaderLength + size)
            {
                return null;
            }

            var dataOffset = position + SegmentHeaderLength;
            if (dataOffset + size > payloadEnd)
            {
                return null;
            }

            entries.Add(new RedguardRobEntry(name, dataOffset, (int)size, type));
            position = dataOffset + size;
        }

        if (position != payloadEnd)
        {
            return null;
        }

        Span<byte> tail = stackalloc byte[TerminatorLength];
        stream.Position = position;
        stream.ReadExactly(tail);

        return tail.SequenceEqual(Terminator) ? new RedguardRobArchive(stream.Name, entries) : null;
    }

    /// <summary>
    ///     A name is 1..8 printable-ASCII bytes followed only by NUL padding. A full 8-character
    ///     name therefore has no terminator of its own — that is the majority case, 3,850 of the
    ///     5,870 retail segments — and what ends it is the type word behind it, whose low byte is
    ///     zero for every type value seen on retail. The reference relies on exactly that, reading
    ///     the name as a C string spanning its two "SegmentID" dwords.
    /// </summary>
    private static string? ReadName(ReadOnlySpan<byte> raw)
    {
        var length = raw.IndexOf((byte)0);
        if (length < 0)
        {
            length = raw.Length;
        }

        if (length == 0)
        {
            return null;
        }

        for (var i = 0; i < length; i++)
        {
            if (raw[i] < 0x20 || raw[i] > 0x7E)
            {
                return null;
            }
        }

        for (var i = length; i < raw.Length; i++)
        {
            if (raw[i] != 0)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(raw[..length]);
    }
}

/// <summary>A parsed ROB archive: the source path and its segments in file order.</summary>
internal sealed class RedguardRobArchive
{
    public RedguardRobArchive(string filePath, IReadOnlyList<RedguardRobEntry> entries)
    {
        FilePath = filePath;
        Entries = entries;
    }

    public string FilePath { get; }

    public IReadOnlyList<RedguardRobEntry> Entries { get; }
}

/// <summary>
///     One ROB segment. <see cref="Size" /> may legitimately be zero — 1,203 of the 5,870 retail
///     segments are empty placeholders that still carry a name — and <see cref="Type" /> is the
///     header's unidentified type word (0 or 512 on all but six retail segments).
/// </summary>
internal readonly record struct RedguardRobEntry(string Name, long Offset, int Size, uint Type);
