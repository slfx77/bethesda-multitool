using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.OblivionPsp;

/// <summary>
///     One resource inside an Oblivion PSP pack entry: what it says it is, where it came from, and
///     where its payload lives.
/// </summary>
/// <param name="TypeName">
///     The declared type, e.g. <c>rwID_CLUMP</c>, <c>rwID_WORLD</c>, <c>rwID_TEXDICTIONARY</c>,
///     <c>rwID_CONVERSATION</c>. Empty when the header carries none.
/// </param>
/// <param name="AuthoringPath">
///     The path the resource was built from on the developers' machine
///     (<c>z:\oblivion\design\work\studioprojects\zones\…</c>), or empty.
/// </param>
/// <param name="HeaderLength">The wrapper's declared header length, read from the body's first u32.</param>
/// <param name="PayloadOffset">Offset of the payload from the start of the containing buffer.</param>
/// <param name="PayloadLength">Bytes of payload.</param>
/// <param name="IsRenderWareStream">
///     True when the payload opens on a well-formed RenderWare chunk header. Only the three
///     geometry/texture types do; animations, audio, conversations and quests carry their own
///     formats and are surfaced with this false rather than being forced through a chunk walk.
/// </param>
/// <param name="RootChunkType">The payload's root RenderWare chunk id, when it is a stream.</param>
/// <param name="LibraryId">The payload's RenderWare library id, when it is a stream.</param>
internal readonly record struct OblivionPspResource(
    string TypeName,
    string AuthoringPath,
    int HeaderLength,
    int PayloadOffset,
    int PayloadLength,
    bool IsRenderWareStream,
    uint RootChunkType,
    uint LibraryId);

/// <summary>
///     Walks the chunk stream inside an Oblivion PSP <c>GR.ARC</c> entry and resolves each named
///     resource to its payload.
///     <para>
///         ⚑ <b>The descriptor-to-payload join, measured 2026-09-06.</b> A pack entry is not a
///         RenderWare stream. It is a flat run of 12-byte <c>{type, size, libraryId}</c> chunks of
///         which three recur: <c>0x071C</c> a class-name registry, <c>0x0716</c> a named resource,
///         and <c>0x0704</c> bulk data. A <c>0x0716</c> BODY is a wrapper — <c>u32 headerLength</c>,
///         a version word, a 16-byte GUID, a length-prefixed type name, then the authoring path —
///         and <b>the payload begins at <c>headerLength + 8</c></b>.
///     </para>
///     <para>
///         That rule was validated against every <c>0x0716</c> in all seven staged builds: it lands
///         on a well-formed RenderWare chunk for 2,463 of them, which is EXACTLY the population of
///         the three RenderWare-payload types — <c>rwID_CLUMP</c> 1,197, <c>rwID_TEXDICTIONARY</c>
///         695, <c>rwID_WORLD</c> 571 — and for none of the other 6,485, whose declared types
///         (<c>rwID_HANIMANIMATION</c> 3,262, <c>rwID_RWS</c> 1,414, <c>rwID_CONVERSATION</c> 188,
///         <c>rwID_QUESTS</c> 93, …) were never RenderWare chunks in the first place. The rule
///         succeeding on precisely the right subset, rather than on most of a population, is what
///         makes it a rule instead of a heuristic.
///     </para>
///     <para>
///         ⚠ <c>0x1300</c> declares a size EIGHT BYTES LARGER than its body. Trusting it
///         desynchronises the walk and silently drops the rest of the entry, so it is corrected
///         here rather than at each call site. 96 such chunks exist across the builds.
///     </para>
///     <para>
///         Everything is little-endian: the PSP is LE, unlike the big-endian J2ME titles in the same
///         Travels family.
///     </para>
/// </summary>
internal static class OblivionPspResourceReader
{
    /// <summary>Chunk id of a named resource wrapper.</summary>
    public const uint NamedResourceChunk = 0x0716;

    /// <summary>Chunk id of a bulk data chunk.</summary>
    public const uint BulkDataChunk = 0x0704;

    /// <summary>Chunk id of the class-name registry.</summary>
    public const uint ClassRegistryChunk = 0x071C;

    /// <summary>
    ///     The chunk whose declared size overshoots its body by <see cref="SizeOvershoot" /> bytes.
    /// </summary>
    public const uint OvershootingChunk = 0x1300;

    /// <summary>How far <see cref="OvershootingChunk" /> overstates its size.</summary>
    public const int SizeOvershoot = 8;

    /// <summary>Bytes of chunk header: type, size, library id.</summary>
    public const int ChunkHeaderLength = 12;

    /// <summary>
    ///     Bytes between the end of a wrapper header and the start of its payload. Measured, not
    ///     derived: <c>headerLength</c> counts the wrapper's own fields and the payload starts eight
    ///     bytes further on.
    /// </summary>
    public const int PayloadGap = 8;

    /// <summary>The RenderWare library id every resolved payload in the retail builds carries.</summary>
    public const uint RetailLibraryId = 0x1C020065;

    /// <summary>
    ///     RenderWare chunk ids accepted as a payload root. Deliberately a closed set: a permissive
    ///     check would call any four bytes a chunk and report a stream where there is none.
    /// </summary>
    private static readonly uint[] StreamRootIds =
    [
        0x01, 0x02, 0x03, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0E, 0x0F, 0x10, 0x14, 0x15, 0x16,
        0x1A, 0x1C
    ];

    /// <summary>
    ///     Reads every named resource in one pack entry. Chunks that are not
    ///     <see cref="NamedResourceChunk" /> are skipped; a malformed size ends the walk rather than
    ///     throwing, because a truncated entry should surface what it has.
    /// </summary>
    public static IReadOnlyList<OblivionPspResource> ReadResources(ReadOnlySpan<byte> entry)
    {
        var resources = new List<OblivionPspResource>();
        var position = 0;

        while (position + ChunkHeaderLength <= entry.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry[position..]);
            var size = ReadChunkSize(entry, position);
            var body = position + ChunkHeaderLength;
            if (size < 0 || size > entry.Length - body)
            {
                break;
            }

            if (type == NamedResourceChunk && size > sizeof(uint))
            {
                resources.Add(ReadResource(entry.Slice(body, size), body));
            }

            position = body + size;
        }

        return resources;
    }

    /// <summary>
    ///     A chunk's payload size with the <see cref="OvershootingChunk" /> correction applied.
    ///     Returns -1 when the declared size cannot be a size.
    /// </summary>
    public static int ReadChunkSize(ReadOnlySpan<byte> buffer, int position)
    {
        if (position + ChunkHeaderLength > buffer.Length)
        {
            return -1;
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(buffer[position..]);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(position + 4)..]);
        if (declared > int.MaxValue)
        {
            return -1;
        }

        var size = (int)declared;
        return type == OvershootingChunk ? Math.Max(0, size - SizeOvershoot) : size;
    }

    /// <summary>Reads one wrapper body, whose start is <paramref name="bodyOffset" /> in the entry.</summary>
    private static OblivionPspResource ReadResource(ReadOnlySpan<byte> body, int bodyOffset)
    {
        var headerLength = (int)Math.Min(
            BinaryPrimitives.ReadUInt32LittleEndian(body), int.MaxValue);

        var typeName = FindTypeName(body, headerLength);
        var authoringPath = FindAuthoringPath(body, headerLength);

        var payloadStart = headerLength + PayloadGap;
        if (headerLength <= 0 || payloadStart < 0 || payloadStart + ChunkHeaderLength > body.Length)
        {
            return new OblivionPspResource(
                typeName, authoringPath, headerLength, bodyOffset + Math.Max(0, payloadStart),
                Math.Max(0, body.Length - Math.Max(0, payloadStart)), false, 0, 0);
        }

        var root = BinaryPrimitives.ReadUInt32LittleEndian(body[payloadStart..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(body[(payloadStart + 4)..]);
        var library = BinaryPrimitives.ReadUInt32LittleEndian(body[(payloadStart + 8)..]);

        var isStream =
            Array.IndexOf(StreamRootIds, root) >= 0 &&
            size > 0 &&
            size <= (uint)(body.Length - payloadStart - ChunkHeaderLength);

        return new OblivionPspResource(
            typeName,
            authoringPath,
            headerLength,
            bodyOffset + payloadStart,
            body.Length - payloadStart,
            isStream,
            isStream ? root : 0,
            isStream ? library : 0);
    }

    /// <summary>
    ///     Finds the <c>rwID_*</c> type name inside the wrapper header. Scanned rather than read at
    ///     a fixed offset because the fields ahead of it are not all pinned; the scan is bounded to
    ///     the header so it can never pick a string out of the payload.
    /// </summary>
    private static string FindTypeName(ReadOnlySpan<byte> body, int headerLength)
    {
        var limit = Math.Clamp(headerLength, 0, body.Length);
        var marker = "rwID_"u8;
        for (var i = 0; i + marker.Length <= limit; i++)
        {
            if (!body.Slice(i, marker.Length).SequenceEqual(marker))
            {
                continue;
            }

            var end = i;
            while (end < limit && IsTypeNameByte(body[end]))
            {
                end++;
            }

            return Encoding.ASCII.GetString(body[i..end]);
        }

        return string.Empty;
    }

    private static bool IsTypeNameByte(byte value) =>
        value is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9') or (byte)'_';

    /// <summary>
    ///     Finds the authoring path — the last NUL-terminated run of printable ASCII in the header
    ///     that looks like a path. Diagnostic only; a resource with none is normal.
    /// </summary>
    private static string FindAuthoringPath(ReadOnlySpan<byte> body, int headerLength)
    {
        var limit = Math.Clamp(headerLength, 0, body.Length);
        var start = -1;
        var best = string.Empty;
        for (var i = 0; i < limit; i++)
        {
            var printable = body[i] is >= 0x20 and < 0x7F;
            if (printable && start < 0)
            {
                start = i;
            }
            else if (!printable && start >= 0)
            {
                var run = Encoding.ASCII.GetString(body[start..i]);
                if (run.Length > best.Length && run.Contains('\\', StringComparison.Ordinal))
                {
                    best = run;
                }

                start = -1;
            }
        }

        return best;
    }
}
