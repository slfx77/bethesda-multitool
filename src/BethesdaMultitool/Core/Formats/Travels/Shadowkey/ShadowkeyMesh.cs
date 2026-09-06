using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>One vertex of one frame, in the mesh's own integer units (the second component is up).</summary>
internal readonly record struct ShadowkeyVertex(short X, short Y, short Z)
{
    /// <summary>The position as a vector, component order unchanged (Y is the up axis).</summary>
    public Vector3 ToVector3()
    {
        return new Vector3(X, Y, Z);
    }
}

/// <summary>
///     One texture coordinate pair. The stored words are <b>8.8 fixed point in texels</b>, not
///     normalised: <see cref="TexelU" /> is <c>U / 256</c>. Values beyond the texture size occur
///     (1,092 of the pack's 36,109 pairs, up to 7.875 x the texture width), so sampling wraps.
/// </summary>
internal readonly record struct ShadowkeyUv(ushort U, ushort V)
{
    /// <summary>Horizontal coordinate in texels (<c>U / 256</c>).</summary>
    public float TexelU => U / 256f;

    /// <summary>Vertical coordinate in texels (<c>V / 256</c>).</summary>
    public float TexelV => V / 256f;

    /// <summary>The pair in texels, as a vector.</summary>
    public Vector2 ToTexels()
    {
        return new Vector2(TexelU, TexelV);
    }
}

/// <summary>
///     One triangle: three vertex indices into the per-frame vertex block and three indices into
///     the UV block. There is <b>no texture index</b> — see <see cref="ShadowkeyTextureSet" />.
/// </summary>
internal readonly record struct ShadowkeyFace(ushort V0, ushort V1, ushort V2, ushort T0, ushort T1, ushort T2);

/// <summary>
///     One animation range, <c>[Start, EndExclusive)</c> over the frame list. <see cref="Rate" />
///     is a playback speed in unknown units (values 1, 3..7, 10, 11, 14, 15, 17, 29 across the 402
///     retail sequences); which range is walk/attack/death is not recorded in the pack.
/// </summary>
internal readonly record struct ShadowkeySequence(ushort Start, ushort EndExclusive, ushort Rate)
{
    /// <summary>Frames the range covers.</summary>
    public int Length => EndExclusive - Start;
}

/// <summary>
///     A mesh's textures: one shared size and <see cref="Skins" /> blocks of <c>width * height</c>
///     0x0RGB 4:4:4 texels, row-major from the top row.
///     <para>
///         Faces carry no texture index, so extra textures are <b>alternative whole-mesh skins</b>,
///         not per-face materials (the 19 multi-texture retail records are all animated character
///         bodies — <c>male_long_tunic.bin</c> alone carries 19 recolours). Which skin the game
///         picks comes from the entity table, not from the pack.
///     </para>
/// </summary>
internal sealed record ShadowkeyTextureSet(int Width, int Height, IReadOnlyList<ushort[]> Skins);

/// <summary>A decoded skin: 8-bit indices plus the palette built from that skin's own texels.</summary>
internal sealed record ShadowkeySkinImage(IndexedBitmap Bitmap, Palette Palette)
{
    /// <summary>Resolves the indices through the palette into RGBA.</summary>
    public DecodedTexture ToDecodedTexture()
    {
        return Bitmap.ToDecodedTexture(Palette);
    }
}

/// <summary>
///     One frame of a mesh unrolled for an exporter: <c>3 * faceCount</c> positions and texel UVs
///     in draw order, with the trivial <c>0, 1, 2, ...</c> index list.
/// </summary>
internal sealed record ShadowkeyMeshTriangles(Vector3[] Positions, Vector2[] TexelUvs, int[] Indices);

/// <summary>
///     One record of Shadowkey's <c>models.huge</c> (N-Gage, little-endian): a keyframe-animated
///     triangle mesh with its own texture set and animation ranges. Original RE 2026-09-05 from the
///     retail pack; there is no licensable reader for this format.
///     <para>
///         Layout, all little-endian, every section immediately after the last:
///     </para>
///     <list type="number">
///         <item>
///             a 14-byte header of seven u16: format tag (7), frame count, vertex count per frame,
///             UV-pair count, face count, <c>3 * vertexCount</c> (a redundant coordinate count) and
///             a constant 1. Tag and the two constants hold on all 226 retail records, so they are
///             checked, not guessed at.
///         </item>
///         <item>
///             <c>frames * vertexCount</c> triples of i16 — <b>frame-major</b>: every frame is a
///             full copy of every position, so the animation is keyframes, not deltas (adjacent
///             frames differ 3-8x less than random pairs across the 33 animated records).
///         </item>
///         <item><c>uvCount</c> pairs of u16 (8.8 fixed point in texels).</item>
///         <item><c>faceCount</c> triangles of six u16: three vertex indices then three UV indices.</item>
///         <item>
///             u16 texture count, u16 width, u16 height, then that many <c>width * height</c> blocks
///             of 0x0RGB 4:4:4 texels (R = bits 11..8, G = 7..4, B = 3..0).
///         </item>
///         <item>u16 sequence count, then that many (start, end, rate) u16 triples.</item>
///     </list>
///     <para>
///         Measured on the retail pack 2026-09-05: all 226 non-empty entries tile to the byte under
///         this walk (678 B <c>arrow.bin</c> to 257,784 B <c>male_long_tunic.bin</c>); every face
///         index is in range; all 1,302,848 texels have a clear top nibble; all 402 sequences satisfy
///         <c>start &lt; end &lt;= frames</c> and every record carries one ending at the last frame.
///         Texture sizes are 64x64 (165 records), 32x32 (31), 16x16 (13), 128x128 (12) and single
///         oddities; no skin uses more than 233 distinct colours, which is why
///         <see cref="DecodeSkin" /> can hand back a 256-entry indexed image.
///     </para>
///     <para>
///         Traps: the second vertex component is the <b>up</b> axis (pine tree 1,897 units tall,
///         humanoid tunic 657) and the unit scale is unknown; UVs are in texels and wrap; the top
///         nibble of a texel is not validated here so a caller can inspect the raw
///         <see cref="ShadowkeyTextureSet.Skins" /> words itself.
///     </para>
/// </summary>
internal sealed class ShadowkeyMesh
{
    /// <summary>Bytes of record header: seven u16.</summary>
    public const int HeaderLength = 14;

    /// <summary>Header word 0 — 7 on all 226 retail records.</summary>
    public const ushort FormatTag = 7;

    /// <summary>Header word 6 — 1 on all 226 retail records; meaning unknown.</summary>
    public const ushort HeaderTrailer = 1;

    /// <summary>
    ///     The 4:4:4 texel that both the mesh textures and the sprites use as a colour key
    ///     (pure magenta, present in 44 of 226 records). That it is the key rather than a painted
    ///     colour is a <b>hypothesis</b> — the renders only ever show it where a hole belongs — so
    ///     <see cref="DecodeSkin" /> takes it as an option, defaulting to transparent.
    /// </summary>
    public const ushort MagentaColourKey = 0x0F0F;

    /// <summary>Palette entries an indexed decode can address.</summary>
    private const int MaxPaletteEntries = Palette.EntryCount;

    private ShadowkeyMesh(
        string name,
        int frameCount,
        int vertexCount,
        IReadOnlyList<ShadowkeyVertex> vertices,
        IReadOnlyList<ShadowkeyUv> uvs,
        IReadOnlyList<ShadowkeyFace> faces,
        ShadowkeyTextureSet textures,
        IReadOnlyList<ShadowkeySequence> sequences)
    {
        Name = name;
        FrameCount = frameCount;
        VertexCount = vertexCount;
        Vertices = vertices;
        Uvs = uvs;
        Faces = faces;
        Textures = textures;
        Sequences = sequences;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Frames of animation; 1 on the 193 static retail records.</summary>
    public int FrameCount { get; }

    /// <summary>Vertices in one frame.</summary>
    public int VertexCount { get; }

    /// <summary>All frames' vertices, frame-major: frame f vertex k is at <c>f * VertexCount + k</c>.</summary>
    public IReadOnlyList<ShadowkeyVertex> Vertices { get; }

    /// <summary>The UV block the faces index (8.8 fixed point in texels).</summary>
    public IReadOnlyList<ShadowkeyUv> Uvs { get; }

    /// <summary>The triangles.</summary>
    public IReadOnlyList<ShadowkeyFace> Faces { get; }

    /// <summary>The alternative skins and their shared size.</summary>
    public ShadowkeyTextureSet Textures { get; }

    /// <summary>The animation ranges.</summary>
    public IReadOnlyList<ShadowkeySequence> Sequences { get; }

    /// <summary>Parses one mesh record, throwing <see cref="InvalidDataException" /> when it does not walk.</summary>
    public static ShadowkeyMesh Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the record is {bytes.Length} bytes, too short for the {HeaderLength}-byte header.");
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        int vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int uvCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int faceCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int coordinateCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        var trailer = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);

        if (tag != FormatTag)
        {
            throw new InvalidDataException(
                $"'{name}': header word 0 at byte 0 is {tag}, expected the format tag {FormatTag}.");
        }

        if (frameCount == 0)
        {
            throw new InvalidDataException($"'{name}': the frame count at byte 2 is 0; a record has at least one frame.");
        }

        if (coordinateCount != 3 * vertexCount)
        {
            throw new InvalidDataException(
                $"'{name}': the coordinate count at byte 10 is {coordinateCount}, expected 3 x {vertexCount} vertices = {3 * vertexCount}.");
        }

        if (trailer != HeaderTrailer)
        {
            throw new InvalidDataException(
                $"'{name}': header word 6 at byte 12 is {trailer}, expected the constant {HeaderTrailer}.");
        }

        var position = (long)HeaderLength;

        var vertices = ReadVertices(bytes, name, frameCount, vertexCount, ref position);
        var uvs = ReadUvs(bytes, name, uvCount, ref position);
        var faces = ReadFaces(bytes, name, faceCount, vertexCount, uvCount, ref position);
        var textures = ReadTextures(bytes, name, ref position);
        var sequences = ReadSequences(bytes, name, frameCount, ref position);

        if (position != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the record's sections end at byte {position} but the entry is {bytes.Length} bytes.");
        }

        return new ShadowkeyMesh(name, frameCount, vertexCount, vertices, uvs, faces, textures, sequences);
    }

    /// <summary>The positions of one frame, in file order (Y up, integer mesh units).</summary>
    public Vector3[] FramePositions(int frame)
    {
        RequireFrame(frame);

        var positions = new Vector3[VertexCount];
        var start = frame * VertexCount;
        for (var i = 0; i < VertexCount; i++)
        {
            positions[i] = Vertices[start + i].ToVector3();
        }

        return positions;
    }

    /// <summary>
    ///     Unrolls one frame into per-corner positions and texel UVs — the shape a GLB/OBJ writer
    ///     wants, since a corner's vertex and UV indices are independent and cannot share one
    ///     index buffer. UVs stay in <b>texels</b>: divide by
    ///     <see cref="ShadowkeyTextureSet.Width" />/<see cref="ShadowkeyTextureSet.Height" /> for
    ///     normalised coordinates, and let the sampler wrap — values past the texture size are
    ///     normal here.
    /// </summary>
    public ShadowkeyMeshTriangles ToUnrolledTriangles(int frame)
    {
        RequireFrame(frame);

        var corners = Faces.Count * 3;
        var positions = new Vector3[corners];
        var uvs = new Vector2[corners];
        var indices = new int[corners];
        var frameBase = frame * VertexCount;

        for (var f = 0; f < Faces.Count; f++)
        {
            var face = Faces[f];
            var corner = f * 3;
            positions[corner] = Vertices[frameBase + face.V0].ToVector3();
            positions[corner + 1] = Vertices[frameBase + face.V1].ToVector3();
            positions[corner + 2] = Vertices[frameBase + face.V2].ToVector3();
            uvs[corner] = Uvs[face.T0].ToTexels();
            uvs[corner + 1] = Uvs[face.T1].ToTexels();
            uvs[corner + 2] = Uvs[face.T2].ToTexels();
            indices[corner] = corner;
            indices[corner + 1] = corner + 1;
            indices[corner + 2] = corner + 2;
        }

        return new ShadowkeyMeshTriangles(positions, uvs, indices);
    }

    /// <summary>
    ///     Decodes one skin into an <see cref="IndexedBitmap" /> plus the <see cref="Palette" />
    ///     built from that skin's own texels: the distinct 4:4:4 values in first-appearance order,
    ///     each nibble replicated to 8 bits (<c>c8 = c4 * 17</c>, so 0xF maps to 0xFF).
    ///     <para>
    ///         With <paramref name="magentaIsTransparent" /> (the default) every palette slot
    ///         holding <see cref="MagentaColourKey" /> gets alpha 0. That the key is transparent is
    ///         a hypothesis, not a proven flag — hence the option.
    ///     </para>
    ///     <para>
    ///         Throws when a skin holds more than 256 distinct colours. No retail skin does (the
    ///         worst is 233 of 319), but the texel encoding allows 4,096.
    ///     </para>
    /// </summary>
    public ShadowkeySkinImage DecodeSkin(int skin, bool magentaIsTransparent = true)
    {
        if (skin < 0 || skin >= Textures.Skins.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(skin), skin, $"'{Name}': the record has {Textures.Skins.Count} skins.");
        }

        var texels = Textures.Skins[skin];
        var indices = new byte[texels.Length];
        var lookup = new Dictionary<ushort, byte>(MaxPaletteEntries);
        var colours = new ushort[MaxPaletteEntries];
        var used = 0;

        for (var i = 0; i < texels.Length; i++)
        {
            var texel = texels[i];
            if (!lookup.TryGetValue(texel, out var index))
            {
                if (used == MaxPaletteEntries)
                {
                    throw new InvalidDataException(
                        $"'{Name}': skin {skin} uses more than {MaxPaletteEntries} distinct colours; texel {i} is the {MaxPaletteEntries + 1}st.");
                }

                index = (byte)used;
                lookup[texel] = index;
                colours[used] = texel;
                used++;
            }

            indices[i] = index;
        }

        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < used; i++)
        {
            var texel = colours[i];
            rgb[i * 3] = Expand4Bit((texel >> 8) & 0xF);
            rgb[(i * 3) + 1] = Expand4Bit((texel >> 4) & 0xF);
            rgb[(i * 3) + 2] = Expand4Bit(texel & 0xF);
        }

        var palette = Palette.FromRgb8(rgb);
        if (magentaIsTransparent)
        {
            for (var i = 0; i < used; i++)
            {
                if (colours[i] == MagentaColourKey)
                {
                    palette = palette.WithTransparentIndex(i);
                }
            }
        }

        var bitmap = new IndexedBitmap(Textures.Width, Textures.Height, indices);
        return new ShadowkeySkinImage(bitmap, palette);
    }

    /// <summary>Replicates a 4-bit channel into 8 bits (<c>v * 17</c>), so 0 -&gt; 0 and 0xF -&gt; 0xFF.</summary>
    private static byte Expand4Bit(int value)
    {
        return (byte)(value * 17);
    }

    private static IReadOnlyList<ShadowkeyVertex> ReadVertices(
        ReadOnlySpan<byte> bytes, string name, int frameCount, int vertexCount, ref long position)
    {
        var count = (long)frameCount * vertexCount;
        RequireRoom(bytes, name, position, count * 6, "the vertex block");

        var vertices = new ShadowkeyVertex[count];
        var offset = (int)position;
        for (var i = 0; i < count; i++)
        {
            vertices[i] = new ShadowkeyVertex(
                BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 2)..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 4)..]));
            offset += 6;
        }

        position = offset;
        return vertices;
    }

    private static IReadOnlyList<ShadowkeyUv> ReadUvs(
        ReadOnlySpan<byte> bytes, string name, int uvCount, ref long position)
    {
        RequireRoom(bytes, name, position, (long)uvCount * 4, "the UV block");

        var uvs = new ShadowkeyUv[uvCount];
        var offset = (int)position;
        for (var i = 0; i < uvCount; i++)
        {
            uvs[i] = new ShadowkeyUv(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]));
            offset += 4;
        }

        position = offset;
        return uvs;
    }

    private static IReadOnlyList<ShadowkeyFace> ReadFaces(
        ReadOnlySpan<byte> bytes, string name, int faceCount, int vertexCount, int uvCount, ref long position)
    {
        RequireRoom(bytes, name, position, (long)faceCount * 12, "the face block");

        var faces = new ShadowkeyFace[faceCount];
        var offset = (int)position;
        for (var i = 0; i < faceCount; i++)
        {
            var face = new ShadowkeyFace(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 8)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 10)..]));

            if (face.V0 >= vertexCount || face.V1 >= vertexCount || face.V2 >= vertexCount)
            {
                throw new InvalidDataException(
                    $"'{name}': face {i} at byte {offset} indexes vertex {Math.Max(face.V0, Math.Max(face.V1, face.V2))} of {vertexCount}.");
            }

            if (face.T0 >= uvCount || face.T1 >= uvCount || face.T2 >= uvCount)
            {
                throw new InvalidDataException(
                    $"'{name}': face {i} at byte {offset} indexes UV {Math.Max(face.T0, Math.Max(face.T1, face.T2))} of {uvCount}.");
            }

            faces[i] = face;
            offset += 12;
        }

        position = offset;
        return faces;
    }

    private static ShadowkeyTextureSet ReadTextures(ReadOnlySpan<byte> bytes, string name, ref long position)
    {
        RequireRoom(bytes, name, position, 6, "the texture header");

        var offset = (int)position;
        int textureCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]);
        offset += 6;

        var texelsPerSkin = (long)width * height;
        RequireRoom(bytes, name, offset, texelsPerSkin * textureCount * 2, "the texture block");

        var skins = new List<ushort[]>(textureCount);
        for (var t = 0; t < textureCount; t++)
        {
            var texels = new ushort[texelsPerSkin];
            for (var i = 0; i < texels.Length; i++)
            {
                texels[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + (i * 2))..]);
            }

            skins.Add(texels);
            offset += (int)(texelsPerSkin * 2);
        }

        position = offset;
        return new ShadowkeyTextureSet(width, height, skins);
    }

    private static IReadOnlyList<ShadowkeySequence> ReadSequences(
        ReadOnlySpan<byte> bytes, string name, int frameCount, ref long position)
    {
        RequireRoom(bytes, name, position, 2, "the sequence count");

        var offset = (int)position;
        int sequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += 2;

        RequireRoom(bytes, name, offset, (long)sequenceCount * 6, "the sequence table");

        var sequences = new ShadowkeySequence[sequenceCount];
        for (var i = 0; i < sequenceCount; i++)
        {
            var sequence = new ShadowkeySequence(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]));

            if (sequence.Start >= sequence.EndExclusive || sequence.EndExclusive > frameCount)
            {
                throw new InvalidDataException(
                    $"'{name}': sequence {i} at byte {offset} is [{sequence.Start}, {sequence.EndExclusive}) over {frameCount} frames.");
            }

            sequences[i] = sequence;
            offset += 6;
        }

        position = offset;
        return sequences;
    }

    private static void RequireRoom(ReadOnlySpan<byte> bytes, string name, long position, long length, string what)
    {
        if (position + length > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {what} needs {length} bytes at byte {position}, past the {bytes.Length}-byte record.");
        }
    }

    private void RequireFrame(int frame)
    {
        if (frame < 0 || frame >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame), frame, $"'{Name}': the record has {FrameCount} frames.");
        }
    }
}
