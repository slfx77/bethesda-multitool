using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>One top-level chunk of a <see cref="VanBurenSceneFile" />.</summary>
/// <param name="Tag">Four-character tag, e.g. <c>VTXD</c>.</param>
/// <param name="Offset">Byte offset of the chunk BODY within the payload.</param>
/// <param name="Length">Body length in bytes.</param>
internal readonly record struct VanBurenSceneChunk(string Tag, int Offset, int Length);

/// <summary>
///     The <c>8TRE</c> scene container from the cancelled Van Buren (Fallout 3) prototype — the
///     per-level geometry blob. Original RE 2026-09-06; <c>kran27/VanBurenTools</c> is GPL and was
///     not consulted for code.
///     <para>
///         Little-endian. <c>+0</c> is the tag <c>8TRE</c>, <c>+4</c> a u32 CONTENT LENGTH, and the
///         content runs from <c>+8</c> for exactly that many bytes as a flat sequence of
///         <c>tag[4] + u32 length + body</c> chunks. ⚠ The payload is LONGER than the content: a
///         trailer of 8-1,132 bytes follows and is NOT part of the chunk stream. Walking to the end
///         of the payload instead of to <c>8 + length</c> fails on 36 of the 39 shipped scenes.
///     </para>
///     <para>
///         ⚑ <b>PARSING IS THE PROOF: the chunks tile <c>[8, 8+length)</c> exactly on 39/39.</b>
///         The top-level tags are ordered and near-fixed — <c>HEAD</c>, <c>MATD</c>, <c>TXTD</c>,
///         <c>VTXD</c> and <c>TREE</c> appear on every scene, with <c>LVLD</c> on 37 and
///         <c>LGTD</c> on 22, giving only THREE distinct sequences across the whole corpus:
///         <c>HEAD LGTD LVLD MATD TXTD VTXD TREE</c> (20), the same without <c>LGTD</c> (17), and
///         the same without <c>LVLD</c> (2).
///     </para>
///     <para>
///         ⚑ The names read as material, texture and vertex data plus a spatial <c>TREE</c> whose
///         body holds nested <c>LEAF</c>/<c>INFO</c> chunks, so this is the geometry the EMAP maps
///         reference. ⚠ Only the CONTAINER is decoded; each chunk's interior is handed back as
///         bytes rather than guessed at.
///     </para>
/// </summary>
internal sealed class VanBurenSceneFile
{
    /// <summary>The tag every scene opens with.</summary>
    public const string Tag = "8TRE";

    /// <summary>Bytes before the chunk stream: the tag and the content length.</summary>
    public const int HeaderLength = 8;

    /// <summary>Bytes of tag plus length ahead of each chunk body.</summary>
    public const int ChunkHeaderLength = 8;

    private VanBurenSceneFile(string name, int contentLength, int trailerLength,
        IReadOnlyList<VanBurenSceneChunk> chunks)
    {
        Name = name;
        ContentLength = contentLength;
        TrailerLength = trailerLength;
        Chunks = chunks;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Bytes of chunk stream, as the header declares.</summary>
    public int ContentLength { get; }

    /// <summary>Bytes after the chunk stream. ⚠ Never zero on the shipped corpus.</summary>
    public int TrailerLength { get; }

    /// <summary>The top-level chunks, in file order.</summary>
    public IReadOnlyList<VanBurenSceneChunk> Chunks { get; }

    /// <summary>Content probe: the tag plus a content length that fits.</summary>
    public static bool IsScene(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength
               && bytes[..4].SequenceEqual("8TRE"u8)
               && HeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) <= bytes.Length;
    }

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenSceneFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var scene, out var error))
        {
            throw new InvalidDataException(error);
        }

        return scene;
    }

    /// <summary>Parses the container, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenSceneFile scene, out string error)
    {
        scene = null!;
        if (!IsScene(bytes))
        {
            error = $"{name}: does not open with '{Tag}' and a content length that fits.";
            return false;
        }

        var contentLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var end = HeaderLength + contentLength;

        var chunks = new List<VanBurenSceneChunk>();
        var at = HeaderLength;
        while (at + ChunkHeaderLength <= end)
        {
            var raw = bytes.Slice(at, 4);

            // ⚠ Without this, a ZERO-FILLED region parses as an endless run of zero-length chunks
            // tagged "\0\0\0\0" and lands exactly on the end, so a padded or truncated file would
            // validate. Every tag on the shipped corpus is four printable characters.
            foreach (var c in raw)
            {
                if (c is < 0x20 or >= 0x7F)
                {
                    error = $"{name}: the bytes at {at} are not a four-character chunk tag.";
                    return false;
                }
            }

            var tag = Encoding.ASCII.GetString(raw);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            var body = at + ChunkHeaderLength;
            if (length < 0 || body + length > end)
            {
                error = $"{name}: chunk '{tag}' at {at} declares {length} bytes, past the content end {end}.";
                return false;
            }

            chunks.Add(new VanBurenSceneChunk(tag, body, length));
            at = body + length;
        }

        // ⚑ THE GATE: the chunks must consume the declared content exactly. A wrong content bound
        // or a mis-read length leaves a remainder here, which is how the trailer was found.
        if (at != end)
        {
            error = $"{name}: chunks end at {at} rather than the declared content end {end}.";
            return false;
        }

        scene = new VanBurenSceneFile(name, contentLength, bytes.Length - end, chunks);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads one chunk's body.</summary>
    public static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> bytes, VanBurenSceneChunk chunk)
    {
        return bytes.Slice(chunk.Offset, chunk.Length);
    }

    /// <summary>The first top-level chunk with a tag, or null.</summary>
    public VanBurenSceneChunk? Find(string tag)
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.Tag == tag)
            {
                return chunk;
            }
        }

        return null;
    }

    /// <summary>
    ///     The texture names the <c>TXTD</c> chunk lists: a dword, a count, then that many
    ///     <c>TXTR</c> sub-chunks each holding a NUL-terminated name. Measured 2026-09-08 on 39/39:
    ///     the list carries the scene's own stem as <c>&lt;stem&gt;_N.ctx</c> (38 of 39; one scene
    ///     lists nothing), which is how a scene is paired with the <c>EMAP</c> that names it when
    ///     no <c>resource.rht</c> is at hand.
    /// </summary>
    public static IReadOnlyList<string> ReadTextureNames(ReadOnlySpan<byte> bytes, VanBurenSceneFile scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var names = new List<string>();
        if (scene.Find("TXTD") is not { } chunk || chunk.Length < 8)
        {
            return names;
        }

        var body = Read(bytes, chunk);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var at = 8;
        while (at + ChunkHeaderLength <= body.Length && names.Count < count)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
            if (!body.Slice(at, 4).SequenceEqual("TXTR"u8) || length < 0 ||
                at + ChunkHeaderLength + length > body.Length)
            {
                break;
            }

            var text = body.Slice(at + ChunkHeaderLength, length);
            var terminator = text.IndexOf((byte)0);
            names.Add(Encoding.ASCII.GetString(terminator < 0 ? text : text[..terminator]));
            at += ChunkHeaderLength + length;
        }

        return names;
    }

    /// <summary>
    ///     The cell grid the <c>LVLD</c> chunk's <c>INFO</c> declares: width, height and the cell
    ///     size on each axis (0.5 on every shipped scene). Absent on the 2 scenes without
    ///     <c>LVLD</c>. ⚠ The <c>LVL0</c> body beside it opens with the dword
    ///     <c>ceil8((width/2) * (height/2))</c> on 37/37 and is otherwise NOT decoded.
    /// </summary>
    public static bool TryReadLevelGrid(ReadOnlySpan<byte> bytes, VanBurenSceneFile scene, out int width,
        out int height, out float cellX, out float cellZ)
    {
        ArgumentNullException.ThrowIfNull(scene);
        width = 0;
        height = 0;
        cellX = 0;
        cellZ = 0;
        if (scene.Find("LVLD") is not { } chunk || chunk.Length < ChunkHeaderLength + 16)
        {
            return false;
        }

        var body = Read(bytes, chunk);
        if (!body[..4].SequenceEqual("INFO"u8) || BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) < 16)
        {
            return false;
        }

        width = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        height = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        cellX = BinaryPrimitives.ReadSingleLittleEndian(body[16..]);
        cellZ = BinaryPrimitives.ReadSingleLittleEndian(body[20..]);
        return width > 0 && height > 0 && cellX > 0 && cellZ > 0;
    }

    /// <summary>
    ///     The root bounds of the octree: the <c>TREE</c> body opens with a <c>NODE</c> whose
    ///     <c>INFO</c> begins with the minimum and maximum corner. On the shipped scenes the box
    ///     spans exactly <c>LVLD</c> width x 0.5 by height x 0.5, so its minimum corner is the
    ///     grid's origin.
    /// </summary>
    public static bool TryReadBounds(ReadOnlySpan<byte> bytes, VanBurenSceneFile scene, out VanBurenVector3 minimum,
        out VanBurenVector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(scene);
        minimum = default;
        maximum = default;
        if (scene.Find("TREE") is not { } chunk || chunk.Length < 2 * ChunkHeaderLength + 24)
        {
            return false;
        }

        var body = Read(bytes, chunk);
        if (!body[..4].SequenceEqual("NODE"u8) || !body.Slice(8, 4).SequenceEqual("INFO"u8)
                                               || BinaryPrimitives.ReadUInt32LittleEndian(body[12..]) < 24)
        {
            return false;
        }

        var floats = body[16..];
        minimum = new VanBurenVector3(
            BinaryPrimitives.ReadSingleLittleEndian(floats),
            BinaryPrimitives.ReadSingleLittleEndian(floats[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(floats[8..]));
        maximum = new VanBurenVector3(
            BinaryPrimitives.ReadSingleLittleEndian(floats[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(floats[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(floats[20..]));
        return true;
    }
}
