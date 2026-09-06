using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.zsk</c> skybox: a small dome or box mesh plus one 512x256
///     palettised image. Little-endian, wrapped in the <see cref="ShadowkeyCompressedFile" />
///     envelope; <see cref="Parse" /> takes the INFLATED payload. The engine's own load step is
///     bracketed by "InitLevel Pre/Post skybox load" and the object it builds is called "Skybox".
///     <code>
///     +0    u16      7                 constant on 21/21
///     +2    u16      1                 constant
///     +4    u16      vertex count      30 (outdoor dome) or 98 (interior box)
///     +6    u16      corner count      168 (outdoor) or 145 (interior)
///     +8    u16      face count        56 (outdoor) or 192 (interior)
///     +10   u16      3 * vertex count  the number of i16 coordinates that follow
///     +12   u16      1                 constant
///     +14   i16[v][3]                  vertex x, y, z
///           u16[c][2]                  corner UVs, 8.8 fixed point
///           u16[f][6]                  face: v0 v1 v2 c0 c1 c2
///           gap                        4 bytes (interior) or 6 (outdoor) - opaque
///           u8[256][512]               sky image, one byte per texel, indexing the zone .pal
///           u16[4]                     footer (1, 0, 1, x) with x = 1 or 10
///     </code>
///     <para>
///         Measured on all 21 retail zones 2026-09-05: the whole file tiles exactly
///         (132,624 bytes outdoor, 134,570 interior) and the image is located from the END, at
///         <c>length - 8 - 131072</c>, because the leading gap is the one part that varies. The
///         outdoor gap carries an extra 0x0001 word that the interior files simply do not have
///         (not a zero), and it is NOT an "image present" flag: four outdoor zones carry it with a
///         blank image. It is left opaque rather than guessed at.
///     </para>
///     <para>
///         Seven distinct bodies serve the 21 zones. All nine interior zones ship one byte-identical
///         98-vertex box with an all-zero image (no sky underground); the twelve outdoor zones share
///         one 30-vertex dome mesh, of which eight carry a painted image (zenith at the centre,
///         horizon around the rim) and four index palette entry 0, the sky colour of those zones.
///         ⚠ CORRECTED 2026-09-06: they do NOT differ only in the texture. <c>raiders</c> carries a
///         different UV TABLE — 35 distinct corner pairs against 44 in the other eleven, with part
///         of the nadir cap collapsed onto the texture centre rather than the rim. Its image is
///         blank, so the fault is invisible in game, but the twelve domes must not be deduplicated
///         on the assumption that their corners match.
///     </para>
/// </summary>
internal sealed class ShadowkeySkybox
{
    /// <summary>Bytes of header before the vertex block.</summary>
    public const int HeaderLength = 14;

    /// <summary>Width of the sky image.</summary>
    public const int TextureWidth = 512;

    /// <summary>Height of the sky image.</summary>
    public const int TextureHeight = 256;

    /// <summary>Bytes in the sky image.</summary>
    public const int TextureLength = TextureWidth * TextureHeight;

    /// <summary>Bytes of footer after the image: the u16 words (1, 0, 1, x).</summary>
    public const int FooterLength = 8;

    /// <summary>Gap between the face table and the image in the 9 interior files.</summary>
    public const int InteriorGapLength = 4;

    /// <summary>Gap between the face table and the image in the 12 outdoor files.</summary>
    public const int OutdoorGapLength = 6;

    private readonly ShadowkeySkyVertex[] _vertices;
    private readonly ShadowkeySkyCorner[] _corners;
    private readonly ShadowkeySkyFace[] _faces;
    private readonly ushort[] _footer;

    private ShadowkeySkybox(
        string name,
        ShadowkeySkyVertex[] vertices,
        ShadowkeySkyCorner[] corners,
        ShadowkeySkyFace[] faces,
        int gapLength,
        IndexedBitmap texture,
        ushort[] footer)
    {
        Name = name;
        _vertices = vertices;
        _corners = corners;
        _faces = faces;
        GapLength = gapLength;
        Texture = texture;
        _footer = footer;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The mesh vertices.</summary>
    public IReadOnlyList<ShadowkeySkyVertex> Vertices => _vertices;

    /// <summary>The texture corners the faces index.</summary>
    public IReadOnlyList<ShadowkeySkyCorner> Corners => _corners;

    /// <summary>The mesh faces.</summary>
    public IReadOnlyList<ShadowkeySkyFace> Faces => _faces;

    /// <summary>
    ///     Bytes between the face table and the image: 4 in the interior files, 6 in the outdoor
    ///     ones. Surfaced raw because the difference is unexplained.
    /// </summary>
    public int GapLength { get; }

    /// <summary>True when this is one of the outdoor bodies (a 6-byte <see cref="GapLength" />).</summary>
    public bool IsOutdoorClass => GapLength == OutdoorGapLength;

    /// <summary>The sky image, 512x256 indices into the zone's <c>.pal</c>.</summary>
    public IndexedBitmap Texture { get; }

    /// <summary>The four footer words: (1, 0, 1, x) with x = 1 in 19 zones and 10 in azra and GlacierCrawl.</summary>
    public IReadOnlyList<ushort> Footer => _footer;

    /// <summary>True when any texel of <see cref="Texture" /> is non-zero (12 of 21 retail zones are blank).</summary>
    public bool HasPaintedTexture
    {
        get
        {
            foreach (var texel in Texture.Indices)
            {
                if (texel != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     Parses an inflated <c>.zsk</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the header constants are wrong, the
    ///     coordinate count disagrees with the vertex count, the blocks do not tile the payload
    ///     with a 4- or 6-byte gap, or a face indexes a vertex or corner that does not exist.
    /// </summary>
    public static ShadowkeySkybox Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the header ends at byte {HeaderLength}, past the {bytes.Length}-byte payload.");
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var one = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var trailingOne = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        if (tag != 7 || one != 1 || trailingOne != 1)
        {
            throw new InvalidDataException(
                $"'{name}': header constants are {tag}/{one}/{trailingOne} at bytes 0, 2 and 12; every retail skybox carries 7/1/1.");
        }

        int vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int cornerCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int faceCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int coordinateCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (coordinateCount != vertexCount * 3)
        {
            throw new InvalidDataException(
                $"'{name}': byte 10 declares {coordinateCount} coordinates but byte 4 declares {vertexCount} vertices (expected {vertexCount * 3}).");
        }

        var cornerOffset = HeaderLength + (vertexCount * 6);
        var faceOffset = cornerOffset + (cornerCount * 4);
        var afterFaces = faceOffset + (faceCount * 12);
        var imageOffset = bytes.Length - FooterLength - TextureLength;
        var gap = imageOffset - afterFaces;
        if (imageOffset < afterFaces || (gap != InteriorGapLength && gap != OutdoorGapLength))
        {
            throw new InvalidDataException(
                $"'{name}': the mesh ends at byte {afterFaces} and the {TextureLength}-byte image starts at {imageOffset}, a gap of {gap}; expected {InteriorGapLength} (interior) or {OutdoorGapLength} (outdoor).");
        }

        var vertices = new ShadowkeySkyVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var offset = HeaderLength + (i * 6);
            vertices[i] = new ShadowkeySkyVertex(
                BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 2)..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 4)..]));
        }

        var corners = new ShadowkeySkyCorner[cornerCount];
        for (var i = 0; i < cornerCount; i++)
        {
            var offset = cornerOffset + (i * 4);
            corners[i] = new ShadowkeySkyCorner(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]));
        }

        var faces = new ShadowkeySkyFace[faceCount];
        for (var i = 0; i < faceCount; i++)
        {
            var offset = faceOffset + (i * 12);
            var face = new ShadowkeySkyFace(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 8)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 10)..]));
            RequireInRange(face.V0, vertexCount, "vertex", offset, name);
            RequireInRange(face.V1, vertexCount, "vertex", offset + 2, name);
            RequireInRange(face.V2, vertexCount, "vertex", offset + 4, name);
            RequireInRange(face.C0, cornerCount, "corner", offset + 6, name);
            RequireInRange(face.C1, cornerCount, "corner", offset + 8, name);
            RequireInRange(face.C2, cornerCount, "corner", offset + 10, name);
            faces[i] = face;
        }

        var texture = new IndexedBitmap(
            TextureWidth, TextureHeight, bytes.Slice(imageOffset, TextureLength).ToArray());

        var footer = new ushort[4];
        for (var i = 0; i < footer.Length; i++)
        {
            footer[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes[(bytes.Length - FooterLength + (i * 2))..]);
        }

        return new ShadowkeySkybox(name, vertices, corners, faces, gap, texture, footer);
    }

    private static void RequireInRange(ushort index, int count, string kind, int offset, string name)
    {
        if (index >= count)
        {
            throw new InvalidDataException(
                $"'{name}': face {kind} index {index} at byte {offset} is past the {count} {kind}s the header declares.");
        }
    }
}
