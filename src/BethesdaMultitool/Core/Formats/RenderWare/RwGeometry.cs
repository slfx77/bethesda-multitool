// The generic RenderWare geometry layout is ported from NeversoftMultitool
//   (https://github.com/slfx77/NeversoftMultitool, MIT License) —
//   src/NeversoftMultitool/Core/Formats/Mesh/RenderWare/{RwGeometry,RwDffDataSections}.cs. The
//   reader here is stricter than upstream's: it predicts the declared struct size from the header
//   and rejects anything that does not match exactly, rather than scanning forward for a plausible
//   chunk when the walk goes wrong. License texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>
///     One triangle. ⚠ The file order is <c>v1, v0, materialIndex, v2</c> — the first two vertex
///     indices are STORED SWAPPED. Reading them in the obvious order silently reverses every
///     triangle's winding, which renders as a mesh turned inside out rather than as an error.
/// </summary>
/// <param name="V0">First corner.</param>
/// <param name="V1">Second corner.</param>
/// <param name="V2">Third corner.</param>
/// <param name="MaterialIndex">Index into the owning geometry's material list.</param>
internal readonly record struct RwTriangle(ushort V0, ushort V1, ushort V2, ushort MaterialIndex);

/// <summary>
///     A generic (non-native) RenderWare geometry: float positions, optional normals, prelit
///     colours and UV sets, plus a triangle list.
///     <para>
///         ⚑ Confirmed to be the form the Oblivion PSP data uses, 2026-09-06: <b>0 of 1,229</b>
///         geometries carry the <c>rwNATIVE</c> flag, and the layout below predicts the declared
///         struct size EXACTLY for <b>1,229 of 1,229</b>. That matters because the sibling tool this
///         is ported from also carries a PS2 native path, and a platform whose geometry had been
///         cooked into native display lists would need that instead — the generic reader would have
///         produced nothing while appearing to work.
///     </para>
/// </summary>
internal sealed class RwGeometry
{
    /// <summary>Triangles are strips rather than a list.</summary>
    public const uint TriangleStripFlag = 0x0001;

    /// <summary>Positions are present.</summary>
    public const uint PositionsFlag = 0x0002;

    /// <summary>One UV set is present (see also <see cref="Textured2Flag" />).</summary>
    public const uint TexturedFlag = 0x0004;

    /// <summary>Per-vertex prelit colours are present.</summary>
    public const uint PrelitFlag = 0x0008;

    /// <summary>Normals are present.</summary>
    public const uint NormalsFlag = 0x0010;

    /// <summary>Two UV sets are present.</summary>
    public const uint Textured2Flag = 0x0080;

    /// <summary>
    ///     Geometry has been cooked to a platform-native form and the generic arrays are absent.
    ///     Never set in the Oblivion PSP data.
    /// </summary>
    public const uint NativeFlag = 0x0100_0000;

    private RwGeometry(
        uint flags, Vector3[] positions, Vector3[]? normals, Vector2[][] uvSets,
        byte[]? colours, RwTriangle[] triangles, Vector4 boundingSphere)
    {
        Flags = flags;
        Positions = positions;
        Normals = normals;
        UvSets = uvSets;
        Colours = colours;
        Triangles = triangles;
        BoundingSphere = boundingSphere;
    }

    /// <summary>The raw flags word.</summary>
    public uint Flags { get; }

    /// <summary>True when the triangles are strips rather than an independent list.</summary>
    public bool IsTriangleStrip => (Flags & TriangleStripFlag) != 0;

    /// <summary>True when the geometry is platform-native and carries no generic arrays.</summary>
    public bool IsNative => (Flags & NativeFlag) != 0;

    /// <summary>Vertex positions of the first morph target.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Vertex normals, or null when the geometry has none.</summary>
    public Vector3[]? Normals { get; }

    /// <summary>UV sets, outer index the set. Empty when untextured.</summary>
    public Vector2[][] UvSets { get; }

    /// <summary>Prelit colours as RGBA bytes, or null. Length is <c>Positions.Length * 4</c>.</summary>
    public byte[]? Colours { get; }

    /// <summary>The triangle list.</summary>
    public RwTriangle[] Triangles { get; }

    /// <summary>The first morph target's bounding sphere: centre in xyz, radius in w.</summary>
    public Vector4 BoundingSphere { get; }

    /// <summary>
    ///     The number of UV sets a flags word declares. Newer streams put the count in bits 16..23;
    ///     older ones express it through <see cref="TexturedFlag" /> / <see cref="Textured2Flag" />.
    ///     Both appear in the PSP data, so both are honoured.
    /// </summary>
    public static int UvSetCount(uint flags)
    {
        var declared = (int)((flags >> 16) & 0xFF);
        if (declared > 0)
        {
            return declared;
        }

        if ((flags & Textured2Flag) != 0)
        {
            return 2;
        }

        return (flags & TexturedFlag) != 0 ? 1 : 0;
    }

    /// <summary>
    ///     The exact byte size the generic layout implies for the given header. Used to validate a
    ///     candidate before reading it — a struct whose declared size does not match is not this
    ///     format, and reading it anyway is how a walk produces confident nonsense.
    /// </summary>
    public static long PredictStructSize(uint flags, int triangleCount, int vertexCount, int morphCount)
    {
        long size = 16;
        if ((flags & PrelitFlag) != 0)
        {
            size += (long)vertexCount * 4;
        }

        size += (long)UvSetCount(flags) * vertexCount * 8;
        size += (long)triangleCount * 8;

        for (var m = 0; m < Math.Max(1, morphCount); m++)
        {
            size += 16 + 4 + 4;
            size += (long)vertexCount * 12;
            if ((flags & NormalsFlag) != 0)
            {
                size += (long)vertexCount * 12;
            }
        }

        return size;
    }

    /// <summary>
    ///     Parses a geometry Struct body. Returns null when the bytes do not match the layout the
    ///     header declares, so a caller walking a whole pack skips rather than aborts.
    /// </summary>
    public static RwGeometry? TryParse(ReadOnlySpan<byte> structBody)
    {
        if (structBody.Length < 16)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(structBody);
        var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(structBody[4..]);
        var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(structBody[8..]);
        var morphCount = BinaryPrimitives.ReadUInt32LittleEndian(structBody[12..]);

        if ((flags & NativeFlag) != 0 ||
            triangleCount > int.MaxValue / 8 || vertexCount > int.MaxValue / 24 || morphCount > 64)
        {
            return null;
        }

        var triangles = (int)triangleCount;
        var vertices = (int)vertexCount;
        if (PredictStructSize(flags, triangles, vertices, (int)morphCount) != structBody.Length)
        {
            return null;
        }

        var position = 16;

        byte[]? colours = null;
        if ((flags & PrelitFlag) != 0)
        {
            colours = structBody.Slice(position, vertices * 4).ToArray();
            position += vertices * 4;
        }

        var uvSets = new Vector2[UvSetCount(flags)][];
        for (var set = 0; set < uvSets.Length; set++)
        {
            var uvs = new Vector2[vertices];
            for (var i = 0; i < vertices; i++)
            {
                uvs[i] = new Vector2(
                    BinaryPrimitives.ReadSingleLittleEndian(structBody[(position + i * 8)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(structBody[(position + i * 8 + 4)..]));
            }

            uvSets[set] = uvs;
            position += vertices * 8;
        }

        var faces = new RwTriangle[triangles];
        for (var i = 0; i < triangles; i++)
        {
            var at = position + i * 8;

            // File order is v1, v0, materialIndex, v2 — the first two are swapped on disk.
            var v1 = BinaryPrimitives.ReadUInt16LittleEndian(structBody[at..]);
            var v0 = BinaryPrimitives.ReadUInt16LittleEndian(structBody[(at + 2)..]);
            var material = BinaryPrimitives.ReadUInt16LittleEndian(structBody[(at + 4)..]);
            var v2 = BinaryPrimitives.ReadUInt16LittleEndian(structBody[(at + 6)..]);
            faces[i] = new RwTriangle(v0, v1, v2, material);
        }

        position += triangles * 8;

        // Only the first morph target is kept: the PSP data has exactly one everywhere, and
        // inventing a representation for the rest would be untested code.
        var sphere = new Vector4(
            BinaryPrimitives.ReadSingleLittleEndian(structBody[position..]),
            BinaryPrimitives.ReadSingleLittleEndian(structBody[(position + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(structBody[(position + 8)..]),
            BinaryPrimitives.ReadSingleLittleEndian(structBody[(position + 12)..]));
        position += 16 + 4 + 4;

        var positions = new Vector3[vertices];
        for (var i = 0; i < vertices; i++)
        {
            positions[i] = ReadVector3(structBody, position + i * 12);
        }

        position += vertices * 12;

        Vector3[]? normals = null;
        if ((flags & NormalsFlag) != 0)
        {
            normals = new Vector3[vertices];
            for (var i = 0; i < vertices; i++)
            {
                normals[i] = ReadVector3(structBody, position + i * 12);
            }
        }

        return new RwGeometry(flags, positions, normals, uvSets, colours, faces, sphere);
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> data, int offset)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]));
    }
}
