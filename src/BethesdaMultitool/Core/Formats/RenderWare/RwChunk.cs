// The RenderWare 3.x chunk walk is ported from NeversoftMultitool
//   (https://github.com/slfx77/NeversoftMultitool, MIT License) —
//   src/NeversoftMultitool/Core/Formats/Mesh/RenderWare/RwChunkReader.cs. Upstream names its
//   constants SCREAMING_SNAKE; they are PascalCase here to satisfy this repo's S101 rule, and the
//   byte-order reads go through BinaryPrimitives rather than BitConverter so the little-endian
//   contract is explicit rather than machine-dependent. Two corrections are applied on the way in —
//   see RwLibraryVersion and the 0x1300 note below. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>One RenderWare chunk header: type, payload size, and the library id that wrote it.</summary>
/// <param name="Type">Chunk type, e.g. <see cref="RwChunk.Clump" />.</param>
/// <param name="Size">Payload bytes following the 12-byte header, after the 0x1300 correction.</param>
/// <param name="LibraryId">The raw library-id word; decode it with <see cref="RwLibraryVersion" />.</param>
/// <param name="PayloadOffset">Offset of the payload in the buffer the header was read from.</param>
internal readonly record struct RwChunkHeader(uint Type, int Size, uint LibraryId, int PayloadOffset)
{
    /// <summary>Offset one past this chunk's payload — where a sibling would start.</summary>
    public int End => PayloadOffset + Size;
}

/// <summary>
///     The RenderWare 3.x chunk walk: a stream of 12-byte <c>{type, size, libraryId}</c> headers,
///     each followed by its payload, nested by type.
///     <para>
///         ⚠ <b>Chunk <see cref="SizeOvershootType" /> (0x1300) declares a size eight bytes larger
///         than its body.</b> Trusting it walks the reader past the following header and the rest of
///         the stream is silently lost — the walk simply ends early with no error, which is the
///         worst shape a parsing fault can take. The correction is applied inside
///         <see cref="TryRead" /> so no caller can forget it. 96 such chunks appear across the
///         Oblivion PSP builds.
///     </para>
///     <para>
///         Everything is little-endian. That is not an assumption: walking the Oblivion PSP streams
///         little-endian lands every nested chunk on a valid header and big-endian does not.
///     </para>
/// </summary>
internal static class RwChunk
{
    /// <summary>Bytes in a chunk header.</summary>
    public const int HeaderLength = 12;

    /// <summary>The chunk whose declared size overshoots its body.</summary>
    public const uint SizeOvershootType = 0x1300;

    /// <summary>How far <see cref="SizeOvershootType" /> overstates its size.</summary>
    public const int SizeOvershoot = 8;

    /// <summary>Struct — the payload carrier inside most typed chunks.</summary>
    public const uint Struct = 0x0001;

    /// <summary>String.</summary>
    public const uint String = 0x0002;

    /// <summary>Extension — where plugin chunks such as BINMESH live.</summary>
    public const uint Extension = 0x0003;

    /// <summary>Texture reference.</summary>
    public const uint Texture = 0x0006;

    /// <summary>Material.</summary>
    public const uint Material = 0x0007;

    /// <summary>Material list.</summary>
    public const uint MaterialList = 0x0008;

    /// <summary>World atomic section.</summary>
    public const uint AtomicSection = 0x0009;

    /// <summary>World plane section.</summary>
    public const uint PlaneSection = 0x000A;

    /// <summary>World — the root of a level's static geometry.</summary>
    public const uint World = 0x000B;

    /// <summary>Frame list.</summary>
    public const uint FrameList = 0x000E;

    /// <summary>Geometry.</summary>
    public const uint Geometry = 0x000F;

    /// <summary>Clump — the root of a model.</summary>
    public const uint Clump = 0x0010;

    /// <summary>Atomic.</summary>
    public const uint Atomic = 0x0014;

    /// <summary>Platform-native texture raster.</summary>
    public const uint TextureNative = 0x0015;

    /// <summary>Texture dictionary.</summary>
    public const uint TextureDictionary = 0x0016;

    /// <summary>Geometry list.</summary>
    public const uint GeometryList = 0x001A;

    /// <summary>Skin plugin.</summary>
    public const uint SkinPlugin = 0x0116;

    /// <summary>
    ///     Binary mesh plugin: per-material index splits. ⚑ Confirmed present in the Oblivion PSP
    ///     data — 8,578 of 8,578 chunks parse as this structure once reached through the pack's
    ///     resource wrapper.
    /// </summary>
    public const uint BinMeshPlugin = 0x050E;

    /// <summary>Chunk types whose payload is itself a chunk stream.</summary>
    private static readonly uint[] ContainerTypes =
    [
        Struct, Extension, Texture, Material, MaterialList, AtomicSection, PlaneSection, World,
        FrameList, Geometry, Clump, Atomic, TextureDictionary, GeometryList
    ];

    /// <summary>True when <paramref name="type" />'s payload is a nested chunk stream.</summary>
    public static bool IsContainer(uint type) => Array.IndexOf(ContainerTypes, type) >= 0;

    /// <summary>
    ///     Reads the header at <paramref name="offset" />, applying the 0x1300 correction and
    ///     rejecting any header whose payload would run past <paramref name="end" />.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> data, int offset, int end, out RwChunkHeader header)
    {
        header = default;
        if (offset < 0 || end > data.Length || end - offset < HeaderLength)
        {
            return false;
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
        var libraryId = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
        if (declared > int.MaxValue)
        {
            return false;
        }

        var size = (int)declared;
        if (type == SizeOvershootType)
        {
            size = Math.Max(0, size - SizeOvershoot);
        }

        var payload = offset + HeaderLength;
        if (size > end - payload)
        {
            return false;
        }

        header = new RwChunkHeader(type, size, libraryId, payload);
        return true;
    }

    /// <summary>
    ///     Enumerates the chunks between <paramref name="offset" /> and <paramref name="end" />,
    ///     stopping at the first malformed header rather than throwing — a truncated stream should
    ///     surface what it has.
    /// </summary>
    public static IEnumerable<RwChunkHeader> Siblings(byte[] data, int offset, int end)
    {
        // Validated here rather than in the iterator: an iterator's checks do not run until the
        // first MoveNext, so a null buffer would surface at the foreach rather than at the call.
        ArgumentNullException.ThrowIfNull(data);
        return Walk(data, offset, end);
    }

    private static IEnumerable<RwChunkHeader> Walk(byte[] data, int offset, int end)
    {
        var position = offset;
        while (TryRead(data, position, end, out var header))
        {
            yield return header;
            position = header.End;
        }
    }

    /// <summary>
    ///     Finds the first direct child of <paramref name="type" /> between
    ///     <paramref name="offset" /> and <paramref name="end" />.
    /// </summary>
    public static bool TryFindChild(
        byte[] data, int offset, int end, uint type, out RwChunkHeader header)
    {
        foreach (var candidate in Siblings(data, offset, end))
        {
            if (candidate.Type == type)
            {
                header = candidate;
                return true;
            }
        }

        header = default;
        return false;
    }

    /// <summary>Reads a NUL-terminated ASCII string, bounded by <paramref name="maxLength" />.</summary>
    public static string ReadString(ReadOnlySpan<byte> data, int offset, int maxLength)
    {
        if (offset < 0 || offset >= data.Length || maxLength <= 0)
        {
            return string.Empty;
        }

        var span = data.Slice(offset, Math.Min(maxLength, data.Length - offset));
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
    }
}
