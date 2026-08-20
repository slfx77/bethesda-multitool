using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Decoder for a Starfield external geometry blob (<c>geometries\&lt;hash&gt;\&lt;hash&gt;.mesh</c>).
///     <para>
///         Starfield moved every vertex/index buffer out of the NIF: in <c>Starfield - Meshes01.ba2</c>
///         288,231 <c>.mesh</c> files live under <c>geometries\</c> and not one <c>.nif</c> does. The NIF
///         is now a pure scene graph whose <c>BSGeometry</c> blocks name a mesh by path, so nothing
///         renders until this blob is decoded.
///     </para>
///     <para>
///         The format is a flat little-endian, count-prefixed stream with no magic string — the first
///         dword is the version (0, 1 or 2). Attributes are quantized: positions are three int16 SNORM
///         scaled by a file-global float, UVs are half-floats, and normals/tangents are packed
///         10/10/10/2. Field order follows NifSkope's BSD-3 <c>src/io/MeshFile.cpp</c>; the trailing
///         meshlet + cull-data section is NOT in that reference and was derived from retail bytes (see
///         <see cref="Parse" />).
///     </para>
/// </summary>
internal sealed class StarfieldMeshFile
{
    /// <summary>Highest container version this decoder understands (retail ships 2).</summary>
    private const uint MaxVersion = 2;

    /// <summary>Positions are int16 SNORM over this range before the file-global scale is applied.</summary>
    private const float SnormScale = 32767f;

    /// <summary>10-bit unsigned channel midpoint: <c>v / 511.5 - 1</c> maps [0,1023] onto [-1,1].</summary>
    private const float Dec10Half = 511.5f;

    /// <summary>X, Y, Z per vertex (length = vertex count * 3), already scaled to mesh-local units.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Three indices per triangle. The stream stores u16, so a blob can never exceed 65,536 verts.</summary>
    public required ushort[] Triangles { get; init; }

    /// <summary>U, V per vertex, or null when the blob carries no UV set.</summary>
    public float[]? Uvs { get; init; }

    /// <summary>Second UV set (lightmap/detail), or null.</summary>
    public float[]? Uvs2 { get; init; }

    /// <summary>R, G, B, A per vertex (source order is BGRA), or null.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>X, Y, Z per vertex, unit length, or null.</summary>
    public float[]? Normals { get; init; }

    /// <summary>X, Y, Z per vertex, or null.</summary>
    public float[]? Tangents { get; init; }

    /// <summary>Per-vertex bitangent handedness (the packed tangent's 2-bit W), or null.</summary>
    public float[]? BitangentSigns { get; init; }

    /// <summary>Bone influences per vertex; 0 for the static meshes the worldspace viewer draws.</summary>
    public int WeightsPerVertex { get; init; }

    /// <summary>
    ///     Bytes the decode consumed. A correct parse lands on exactly <c>data.Length</c>; the corpus
    ///     test asserts that across many files, which is what proves the field order is right rather
    ///     than merely self-consistent.
    /// </summary>
    public int BytesConsumed { get; init; }

    /// <summary>
    ///     Decodes a <c>.mesh</c> blob, or returns null when it is truncated, declares a version this
    ///     decoder does not know, or carries a non-positive position scale (which the format uses as an
    ///     "invalid" marker).
    /// </summary>
    public static StarfieldMeshFile? Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        if (!TryReadU32(data, ref pos, out var version) || version > MaxVersion)
        {
            return null;
        }

        if (!TryReadU32(data, ref pos, out var indexCount))
        {
            return null;
        }

        // Indices are u16 triples. This is also why no submesh split is needed downstream: the format
        // cannot express a vertex index above 65,535, so the renderer's R16_UInt index buffer fits.
        // Read the DECLARED count (so the stream position stays exact) but expose only whole triangles.
        if (!Fits(data, pos, indexCount, 2))
        {
            return null;
        }

        var indices = new ushort[indexCount];
        if (!TryReadU16Array(data, ref pos, indices))
        {
            return null;
        }

        var triangles = indexCount % 3 == 0 ? indices : indices[..(int)(indexCount - indexCount % 3)];

        if (!TryReadF32(data, ref pos, out var scale) || scale <= 0f || !float.IsFinite(scale))
        {
            return null; // the format's own validity check
        }

        if (!TryReadU32(data, ref pos, out var weightsPerVertex) ||
            !TryReadU32(data, ref pos, out var vertexCount) ||
            vertexCount == 0 ||
            !Fits(data, pos, vertexCount, 6)) // u32 XY + u16 Z
        {
            return null;
        }

        // Positions: a u32 holding int16 X and Y, then a separate u16 holding int16 Z — 6 bytes, NOT
        // an 8-byte aligned struct.
        var positions = new float[vertexCount * 3];
        var perVertexScale = scale / SnormScale;
        for (var i = 0; i < vertexCount; i++)
        {
            if (!TryReadU32(data, ref pos, out var xy) || !TryReadU16(data, ref pos, out var zRaw))
            {
                return null;
            }

            positions[i * 3 + 0] = (short)(xy & 0xFFFF) * perVertexScale;
            positions[i * 3 + 1] = (short)(xy >> 16) * perVertexScale;
            positions[i * 3 + 2] = (short)zRaw * perVertexScale;
        }

        if (!TryReadHalfPairs(data, ref pos, out var uvs) ||
            !TryReadHalfPairs(data, ref pos, out var uvs2) ||
            !TryReadColors(data, ref pos, out var colors) ||
            !TryReadDec4(data, ref pos, out var normals, out _) ||
            !TryReadDec4(data, ref pos, out var tangents, out var bitangentSigns))
        {
            return null;
        }

        // Skin weights (u16 bone + u16 weight each). Parsed for stream position only — the worldspace
        // viewer draws statics in bind pose and skinned actors are out of scope.
        if (!TrySkipCounted(data, ref pos, 4))
        {
            return null;
        }

        if (version != 0 && !TrySkipLods(data, ref pos))
        {
            return null;
        }

        // Trailing meshlet + cull-data section. NOT present in the NifSkope reference; recovered from
        // retail bytes: a 6-vertex/2-triangle blob declares one meshlet (6, 0, 2, 0) — vertex count,
        // vertex offset, triangle count, triangle offset — and one cull record of six floats that is a
        // CENTRE + EXTENT pair, not a min/max box (vertex 0 of that blob sits exactly on two of the
        // extents). Skipped rather than modelled: GPU meshlet culling is not something this renderer
        // does, but consuming it is what lets the parse land on the exact final byte.
        if (!TrySkipCounted(data, ref pos, 16) || // meshlets
            !TrySkipCounted(data, ref pos, 24)) // cull data
        {
            return null;
        }

        return new StarfieldMeshFile
        {
            Positions = positions,
            Triangles = triangles,
            Uvs = uvs,
            Uvs2 = uvs2,
            VertexColors = colors,
            Normals = normals,
            Tangents = tangents,
            BitangentSigns = bitangentSigns,
            WeightsPerVertex = (int)weightsPerVertex,
            BytesConsumed = pos
        };
    }

    /// <summary>Reads a count-prefixed array of half-float UV pairs, or null when the count is zero.</summary>
    private static bool TryReadHalfPairs(ReadOnlySpan<byte> data, ref int pos, out float[]? result)
    {
        result = null;
        if (!TryReadU32(data, ref pos, out var count))
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (!Fits(data, pos, count, 4))
        {
            return false;
        }

        var uvs = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadU32(data, ref pos, out var packed))
            {
                return false;
            }

            uvs[i * 2 + 0] = (float)BitConverter.UInt16BitsToHalf((ushort)(packed & 0xFFFF));
            uvs[i * 2 + 1] = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16));
        }

        result = uvs;
        return true;
    }

    /// <summary>Reads count-prefixed BGRA vertex colours, re-ordered to RGBA.</summary>
    private static bool TryReadColors(ReadOnlySpan<byte> data, ref int pos, out byte[]? result)
    {
        result = null;
        if (!TryReadU32(data, ref pos, out var count))
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (!Fits(data, pos, count, 4))
        {
            return false;
        }

        var colors = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadU32(data, ref pos, out var bgra))
            {
                return false;
            }

            colors[i * 4 + 0] = (byte)(bgra >> 16); // R
            colors[i * 4 + 1] = (byte)(bgra >> 8); // G
            colors[i * 4 + 2] = (byte)bgra; // B
            colors[i * 4 + 3] = (byte)(bgra >> 24); // A
        }

        result = colors;
        return true;
    }

    /// <summary>
    ///     Reads a count-prefixed array of 10/10/10/2-packed direction vectors. Each 10-bit channel is
    ///     unsigned [0,1023] mapped onto [-1,1]; the 2-bit W is returned separately because on the
    ///     tangent array it carries bitangent handedness.
    /// </summary>
    private static bool TryReadDec4(ReadOnlySpan<byte> data, ref int pos, out float[]? result, out float[]? w)
    {
        result = null;
        w = null;
        if (!TryReadU32(data, ref pos, out var count))
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (!Fits(data, pos, count, 4))
        {
            return false;
        }

        var vectors = new float[count * 3];
        var ws = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadU32(data, ref pos, out var packed))
            {
                return false;
            }

            vectors[i * 3 + 0] = (packed & 0x3FF) / Dec10Half - 1f;
            vectors[i * 3 + 1] = ((packed >> 10) & 0x3FF) / Dec10Half - 1f;
            vectors[i * 3 + 2] = ((packed >> 20) & 0x3FF) / Dec10Half - 1f;
            ws[i] = ((packed >> 30) & 0x3) == 0 ? -1f : 1f;
        }

        result = vectors;
        w = ws;
        return true;
    }

    /// <summary>Skips the LOD section: a count, then per LOD a u16-index triangle list.</summary>
    private static bool TrySkipLods(ReadOnlySpan<byte> data, ref int pos)
    {
        if (!TryReadU32(data, ref pos, out var lodCount))
        {
            return false;
        }

        for (var i = 0; i < lodCount; i++)
        {
            if (!TryReadU32(data, ref pos, out var indexCount) ||
                !Fits(data, pos, indexCount, 2))
            {
                return false;
            }

            pos += (int)indexCount * 2;
        }

        return true;
    }

    /// <summary>Skips a count-prefixed array of fixed-size records.</summary>
    private static bool TrySkipCounted(ReadOnlySpan<byte> data, ref int pos, int stride)
    {
        if (!TryReadU32(data, ref pos, out var count) || !Fits(data, pos, count, stride))
        {
            return false;
        }

        pos += (int)count * stride;
        return true;
    }

    /// <summary>
    ///     Whether <paramref name="count" /> records of <paramref name="elementSize" /> bytes still fit
    ///     in the buffer. Checked in 64-bit BEFORE any int multiply so a corrupt or hostile count
    ///     (counts are unvalidated file input) can never overflow into a small positive length — the
    ///     decoder's contract is to return null on bad input, never to throw.
    /// </summary>
    private static bool Fits(ReadOnlySpan<byte> data, int pos, uint count, int elementSize)
    {
        return count * elementSize <= data.Length - (long)pos;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> data, ref int pos, out uint value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        pos += 4;
        return true;
    }

    private static bool TryReadU16(ReadOnlySpan<byte> data, ref int pos, out ushort value)
    {
        if (pos + 2 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;
        return true;
    }

    private static bool TryReadF32(ReadOnlySpan<byte> data, ref int pos, out float value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0f;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(data[pos..]);
        pos += 4;
        return true;
    }

    private static bool TryReadU16Array(ReadOnlySpan<byte> data, ref int pos, ushort[] destination)
    {
        if (pos + destination.Length * 2 > data.Length)
        {
            return false;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + i * 2)..]);
        }

        pos += destination.Length * 2;
        return true;
    }
}
