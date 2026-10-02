using System.Buffers.Binary;
using System.Globalization;

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
///     <para>
///         The meshlet + cull tail is OPTIONAL (cut-2 plan <c>docs/design/cut2-starfield-mesh-reader-plan-20260928.md</c>,
///         section 0.2, measured over all 720,957 retail <c>.mesh</c> entries of the 11 archives that hold
///         <c>geometries\</c>): 6,470 files end exactly after the LOD section (all 4,832 of
///         <c>Starfield - FaceMeshes.ba2</c> among them) and 714,487 carry the tail, and every one of the 720,957 decodes to
///         its last byte under that rule. The first normal's 2-bit W says which (0 without the tail, 1 with it, a two-way
///         equality over the whole corpus); this decoder does not use W, so a stream truncated exactly at the tail
///         boundary still parses as tail-less here, and the model reader refuses it by that rule. An earlier check of the
///         layout drew 3,003 files from <c>Meshes01</c> only, where every file has the tail.
///     </para>
///     <para>
///         Two numeric routes coexist. The legacy members (<see cref="Positions" />, <see cref="Normals" />,
///         <see cref="Tangents" />) keep the renderer's float32 arithmetic (two roundings), unchanged, so warm disk-cache
///         entries and the viewer stay bit-identical. The raw members (<see cref="QuantizedPositions" />,
///         <see cref="NormalCodes" />, ...) keep the stored values, and <see cref="PositionMeters" /> and
///         <see cref="Dec4Channel" /> are the correctly rounded routes the model reader types (plan decision D5).
///     </para>
/// </summary>
internal sealed class StarfieldMeshFile
{
    /// <summary>Highest container version this decoder understands (retail ships 2).</summary>
    private const uint MaxVersion = 2;

    /// <summary>Positions are int16 SNORM over this range before the file-global scale is applied.</summary>
    private const float SnormScale = 32767f;

    /// <summary>The SNORM range in binary64, for the one-rounding route of <see cref="PositionMeters" />.</summary>
    private const double SnormScaleExact = 32767.0;

    /// <summary>10-bit unsigned channel midpoint: <c>v / 511.5 - 1</c> maps [0,1023] onto [-1,1].</summary>
    private const float Dec10Half = 511.5f;

    /// <summary>The failure reason for a field that does not fit in the remaining bytes.</summary>
    private const string PastEnd = "runs past the end of the stream";

    /// <summary>
    ///     The correctly rounded float32 of <c>(2v - 1023) / 1023</c> for every 10-bit code <c>v</c>, evaluated once in
    ///     binary64 and rounded once (plan decision D5; equal to the exact rational rounding on all 1,024 codes, while the
    ///     legacy float32 route <c>v / 511.5f - 1f</c> differs on 556 of them).
    /// </summary>
    private static readonly float[] Dec4Channels = CreateDec4Channels();

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

    /// <summary>The container version dword (0, 1 or 2; every retail file says 2).</summary>
    public uint Version { get; init; }

    /// <summary>
    ///     The index count the stream declares. <see cref="Triangles" /> keeps only whole triangles (the legacy trim); the
    ///     model reader refuses a count that is not a multiple of 3 instead (no retail file has one).
    /// </summary>
    public uint IndexCount { get; init; }

    /// <summary>Every declared index as stored, the incomplete last triangle included.</summary>
    public ushort[] Indices { get; init; } = [];

    /// <summary>The file-global position scale as stored (finite and positive, or the parse fails).</summary>
    public float Scale { get; init; }

    /// <summary>The stored int16 position components, X, Y, Z per vertex, before any scaling.</summary>
    public short[] QuantizedPositions { get; init; } = [];

    /// <summary>The first UV set's stored half bits, U, V per vertex, or null when the set is empty.</summary>
    public ushort[]? Uv0Bits { get; init; }

    /// <summary>The second UV set's stored half bits, U, V per vertex, or null when the set is empty.</summary>
    public ushort[]? Uv1Bits { get; init; }

    /// <summary>The vertex colors exactly as stored, B, G, R, A per vertex, or null when there are none.</summary>
    public byte[]? ColorBytes { get; init; }

    /// <summary>The packed 10/10/10/2 normal words as stored, one per vertex, or null when there are none.</summary>
    public uint[]? NormalCodes { get; init; }

    /// <summary>The packed 10/10/10/2 tangent words as stored (bitangent sign in the 2-bit W), or null.</summary>
    public uint[]? TangentCodes { get; init; }

    /// <summary>
    ///     The skin weight pairs as stored, bone index then weight, interleaved (two entries per pair), or null when the
    ///     stream stores none. The stream count is independent of <see cref="WeightsPerVertex" />; the model reader checks
    ///     that it equals the vertex count times that value.
    /// </summary>
    public ushort[]? WeightPairs { get; init; }

    /// <summary>The LOD index lists as stored, in stream order (empty for version 0, which has no LOD section).</summary>
    public IReadOnlyList<ushort[]> LodIndexLists { get; init; } = [];

    /// <summary>Whether the stream carries the meshlet and cull tail (false when it ends exactly after the LOD section).</summary>
    public bool HasMeshletTail { get; init; }

    /// <summary>
    ///     The meshlet records as stored, four words each (vertex count, vertex offset, triangle count, triangle offset);
    ///     empty without the tail.
    /// </summary>
    public uint[] Meshlets { get; init; } = [];

    /// <summary>The cull records as stored, six floats each (center X, Y, Z, then extent X, Y, Z); empty without the tail.</summary>
    public float[] CullRecords { get; init; } = [];

    /// <summary>Every field and section the parse walked, in stream order, tiling bytes 0 to <see cref="BytesConsumed" />.</summary>
    public IReadOnlyList<StarfieldMeshSection> Sections { get; init; } = [];

    /// <summary>
    ///     The correctly rounded position of one stored component: <c>q * scale / 32767</c> evaluated in binary64 (the
    ///     product is exact there) and rounded once to float32. Plan decision D5: it recovers <c>q</c> exactly on every
    ///     sampled retail component, while the legacy <see cref="Positions" /> route (float32 <c>scale / 32767</c>, then
    ///     the float32 product) differs from it on 5.99% of them.
    /// </summary>
    public static float PositionMeters(short quantized, float scale)
    {
        return (float)(quantized * (double)scale / SnormScaleExact);
    }

    /// <summary>
    ///     The correctly rounded value of one 10-bit Dec4 channel code (the low 10 bits of <paramref name="code" />):
    ///     float32 of <c>(2v - 1023) / 1023</c>. Code 511 (the zero sentinel) is <c>-1/1023</c>, not zero.
    /// </summary>
    public static float Dec4Channel(uint code)
    {
        return Dec4Channels[(int)(code & 0x3FF)];
    }

    /// <summary>
    ///     Decodes a <c>.mesh</c> blob, or returns null when it is truncated, declares a version this
    ///     decoder does not know, or carries a non-positive position scale (which the format uses as an
    ///     "invalid" marker).
    /// </summary>
    public static StarfieldMeshFile? Parse(ReadOnlySpan<byte> data)
    {
        return Decode(data, out _);
    }

    /// <summary>
    ///     <see cref="Parse" /> with the reason when it fails: the field being read, its offset and the rule the bytes
    ///     broke. Never throws on bad input.
    /// </summary>
    internal static StarfieldMeshFile? Decode(ReadOnlySpan<byte> data, out StarfieldMeshParseFailure? failure)
    {
        var sections = new List<StarfieldMeshSection>(16);
        var pos = 0;
        if (!TryReadU32(data, ref pos, out var version))
        {
            return Fail(out failure, "version", 0, PastEnd);
        }

        if (version > MaxVersion)
        {
            return Fail(out failure, "version", 0, string.Create(CultureInfo.InvariantCulture,
                $"{version} is above the highest known version {MaxVersion}"));
        }

        sections.Add(new StarfieldMeshSection("version", 0, 4, 1));

        // Indices are u16 triples. This is also why no submesh split is needed downstream: the format
        // cannot express a vertex index above 65,535, so the renderer's R16_UInt index buffer fits.
        // Read the DECLARED count (so the stream position stays exact) but expose only whole triangles.
        if (!TryReadSection(data, ref pos, "indices", 2, sections, out var indexCount, out var indexPayload,
                out failure))
        {
            return null;
        }

        var indices = ReadU16s(data, indexPayload, indexCount);
        var triangles = indexCount % 3 == 0 ? indices : indices[..(int)(indexCount - indexCount % 3)];

        var scaleAt = pos;
        if (!TryReadF32(data, ref pos, out var scale))
        {
            return Fail(out failure, "scale", scaleAt, PastEnd);
        }

        if (scale <= 0f || !float.IsFinite(scale))
        {
            // The format's own validity check.
            return Fail(out failure, "scale", scaleAt, string.Create(CultureInfo.InvariantCulture,
                $"{scale:R} is not finite and positive"));
        }

        sections.Add(new StarfieldMeshSection("scale", scaleAt, 4, 1));

        var weightsPerVertexAt = pos;
        if (!TryReadU32(data, ref pos, out var weightsPerVertex))
        {
            return Fail(out failure, "weightsPerVertex", weightsPerVertexAt, PastEnd);
        }

        sections.Add(new StarfieldMeshSection("weightsPerVertex", weightsPerVertexAt, 4, 1));

        // Positions: a u32 holding int16 X and Y, then a separate u16 holding int16 Z — 6 bytes, NOT
        // an 8-byte aligned struct.
        var positionsAt = pos;
        if (!TryReadSection(data, ref pos, "positions", 6, sections, out var vertexCount, out var positionPayload,
                out failure))
        {
            return null;
        }

        if (vertexCount == 0)
        {
            return Fail(out failure, "positions", positionsAt, "declares no vertices");
        }

        var positions = new float[vertexCount * 3];
        var quantized = new short[vertexCount * 3];
        var perVertexScale = scale / SnormScale;
        for (var i = 0; i < vertexCount; i++)
        {
            var at = positionPayload + i * 6;
            var xy = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            var zRaw = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
            quantized[i * 3 + 0] = (short)(xy & 0xFFFF);
            quantized[i * 3 + 1] = (short)(xy >> 16);
            quantized[i * 3 + 2] = (short)zRaw;
            positions[i * 3 + 0] = (short)(xy & 0xFFFF) * perVertexScale;
            positions[i * 3 + 1] = (short)(xy >> 16) * perVertexScale;
            positions[i * 3 + 2] = (short)zRaw * perVertexScale;
        }

        if (!TryReadSection(data, ref pos, "uv0", 4, sections, out var uv0Count, out var uv0Payload, out failure) ||
            !TryReadSection(data, ref pos, "uv1", 4, sections, out var uv1Count, out var uv1Payload, out failure) ||
            !TryReadSection(data, ref pos, "colors", 4, sections, out var colorCount, out var colorPayload,
                out failure) ||
            !TryReadSection(data, ref pos, "normals", 4, sections, out var normalCount, out var normalPayload,
                out failure) ||
            !TryReadSection(data, ref pos, "tangents", 4, sections, out var tangentCount, out var tangentPayload,
                out failure))
        {
            return null;
        }

        var (uvs, uv0Bits) = DecodeHalfPairs(data, uv0Payload, uv0Count);
        var (uvs2, uv1Bits) = DecodeHalfPairs(data, uv1Payload, uv1Count);
        var (colors, colorBytes) = DecodeColors(data, colorPayload, colorCount);
        var (normals, _, normalCodes) = DecodeDec4(data, normalPayload, normalCount);
        var (tangents, bitangentSigns, tangentCodes) = DecodeDec4(data, tangentPayload, tangentCount);

        // Skin weights (u16 bone + u16 weight each). The worldspace viewer draws statics in bind pose and ignores
        // them; the model reader types them as per-slot streams.
        if (!TryReadSection(data, ref pos, "weights", 4, sections, out var weightCount, out var weightPayload,
                out failure))
        {
            return null;
        }

        var weightPairs = weightCount == 0 ? null : ReadU16s(data, weightPayload, weightCount * 2u);

        List<ushort[]> lods = [];
        if (version != 0)
        {
            var lodsAt = pos;
            if (!TryReadU32(data, ref pos, out var lodCount))
            {
                return Fail(out failure, "lods", lodsAt, PastEnd);
            }

            var lodSections = new List<StarfieldMeshSection>();
            for (var lod = 0u; lod < lodCount; lod++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"lod:{lod + 1}");
                if (!TryReadSection(data, ref pos, name, 2, lodSections, out var lodIndexCount, out var lodPayload,
                        out failure))
                {
                    return null;
                }

                lods.Add(ReadU16s(data, lodPayload, lodIndexCount));
            }

            sections.Add(new StarfieldMeshSection("lods", lodsAt, pos - lodsAt, lodCount));
            sections.AddRange(lodSections);
        }

        // Trailing meshlet + cull-data section. NOT present in the NifSkope reference; recovered from
        // retail bytes: a 6-vertex/2-triangle blob declares one meshlet (6, 0, 2, 0) — vertex count,
        // vertex offset, triangle count, triangle offset — and one cull record of six floats that is a
        // CENTRE + EXTENT pair, not a min/max box (vertex 0 of that blob sits exactly on two of the
        // extents). Kept raw rather than modelled: GPU meshlet culling is not something this renderer
        // does, but consuming it is what lets the parse land on the exact final byte. The tail is optional:
        // a stream that ends exactly here has none (6,470 retail files).
        var hasTail = pos != data.Length;
        uint[] meshlets = [];
        float[] cull = [];
        if (hasTail)
        {
            if (!TryReadSection(data, ref pos, "meshlets", 16, sections, out var meshletCount, out var meshletPayload,
                    out failure))
            {
                return null;
            }

            meshlets = ReadU32s(data, meshletPayload, meshletCount * 4u);
            if (!TryReadSection(data, ref pos, "cull", 24, sections, out var cullCount, out var cullPayload,
                    out failure))
            {
                return null;
            }

            cull = ReadF32s(data, cullPayload, cullCount * 6u);
        }

        failure = null;
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
            BytesConsumed = pos,
            Version = version,
            IndexCount = indexCount,
            Indices = indices,
            Scale = scale,
            QuantizedPositions = quantized,
            Uv0Bits = uv0Bits,
            Uv1Bits = uv1Bits,
            ColorBytes = colorBytes,
            NormalCodes = normalCodes,
            TangentCodes = tangentCodes,
            WeightPairs = weightPairs,
            LodIndexLists = lods.AsReadOnly(),
            HasMeshletTail = hasTail,
            Meshlets = meshlets,
            CullRecords = cull,
            Sections = sections.AsReadOnly()
        };
    }

    /// <summary>Records a failure and returns null (the decoder's only failure value).</summary>
    private static StarfieldMeshFile? Fail(out StarfieldMeshParseFailure? failure, string field, int offset,
        string reason)
    {
        failure = new StarfieldMeshParseFailure(field, offset, reason);
        return null;
    }

    /// <summary>
    ///     Reads a section's count dword, checks that <paramref name="stride" />-byte elements of that count fit, steps
    ///     past them and records the section. On failure the position is unspecified and the reason says which rule broke.
    /// </summary>
    private static bool TryReadSection(ReadOnlySpan<byte> data, ref int pos, string name, int stride,
        List<StarfieldMeshSection> sections, out uint count, out int payload, out StarfieldMeshParseFailure? failure)
    {
        var start = pos;
        payload = 0;
        if (!TryReadU32(data, ref pos, out count))
        {
            failure = new StarfieldMeshParseFailure(name, start, PastEnd);
            return false;
        }

        if (!Fits(data, pos, count, stride))
        {
            failure = new StarfieldMeshParseFailure(name, start, string.Create(CultureInfo.InvariantCulture,
                $"{count} elements of {stride} bytes run past the end of the stream"));
            return false;
        }

        payload = pos;
        pos += (int)((long)count * stride);
        sections.Add(new StarfieldMeshSection(name, start, pos - start, count));
        failure = null;
        return true;
    }

    /// <summary>
    ///     Decodes a section of half-float UV pairs: the legacy float values and the stored bits, both null when the
    ///     count is zero.
    /// </summary>
    private static (float[]? Values, ushort[]? Bits) DecodeHalfPairs(ReadOnlySpan<byte> data, int payload, uint count)
    {
        if (count == 0)
        {
            return (null, null);
        }

        var bits = ReadU16s(data, payload, count * 2u);
        var uvs = new float[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            uvs[i] = (float)BitConverter.UInt16BitsToHalf(bits[i]);
        }

        return (uvs, bits);
    }

    /// <summary>Decodes BGRA vertex colors: the legacy RGBA relayout and the stored bytes, both null when there are none.</summary>
    private static (byte[]? Rgba, byte[]? Stored) DecodeColors(ReadOnlySpan<byte> data, int payload, uint count)
    {
        if (count == 0)
        {
            return (null, null);
        }

        var stored = data.Slice(payload, (int)count * 4).ToArray();
        var colors = new byte[stored.Length];
        for (var i = 0; i < count; i++)
        {
            var bgra = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(i * 4));
            colors[i * 4 + 0] = (byte)(bgra >> 16); // R
            colors[i * 4 + 1] = (byte)(bgra >> 8); // G
            colors[i * 4 + 2] = (byte)bgra; // B
            colors[i * 4 + 3] = (byte)(bgra >> 24); // A
        }

        return (colors, stored);
    }

    /// <summary>
    ///     Decodes a section of 10/10/10/2-packed direction vectors. Each 10-bit channel is unsigned [0,1023] mapped onto
    ///     [-1,1] by the legacy float32 route; the 2-bit W is returned separately because on the tangent array it carries
    ///     bitangent handedness (code 0 is -1, any other code +1), and the stored words are returned as they are. All
    ///     three are null when the count is zero.
    /// </summary>
    private static (float[]? Vectors, float[]? W, uint[]? Codes) DecodeDec4(ReadOnlySpan<byte> data, int payload,
        uint count)
    {
        if (count == 0)
        {
            return (null, null, null);
        }

        var codes = ReadU32s(data, payload, count);
        var vectors = new float[count * 3];
        var ws = new float[count];
        for (var i = 0; i < codes.Length; i++)
        {
            var packed = codes[i];
            vectors[i * 3 + 0] = (packed & 0x3FF) / Dec10Half - 1f;
            vectors[i * 3 + 1] = ((packed >> 10) & 0x3FF) / Dec10Half - 1f;
            vectors[i * 3 + 2] = ((packed >> 20) & 0x3FF) / Dec10Half - 1f;
            ws[i] = ((packed >> 30) & 0x3) == 0 ? -1f : 1f;
        }

        return (vectors, ws, codes);
    }

    /// <summary>Reads <paramref name="count" /> little-endian u16 values at a payload offset already checked to fit.</summary>
    private static ushort[] ReadU16s(ReadOnlySpan<byte> data, int payload, uint count)
    {
        var values = new ushort[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(payload + i * 2)..]);
        }

        return values;
    }

    /// <summary>Reads <paramref name="count" /> little-endian u32 values at a payload offset already checked to fit.</summary>
    private static uint[] ReadU32s(ReadOnlySpan<byte> data, int payload, uint count)
    {
        var values = new uint[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(payload + i * 4)..]);
        }

        return values;
    }

    /// <summary>Reads <paramref name="count" /> little-endian f32 values at a payload offset already checked to fit.</summary>
    private static float[] ReadF32s(ReadOnlySpan<byte> data, int payload, uint count)
    {
        var values = new float[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(payload + i * 4)..]);
        }

        return values;
    }

    /// <summary>Builds <see cref="Dec4Channels" />: one binary64 division per code, rounded once to float32.</summary>
    private static float[] CreateDec4Channels()
    {
        var table = new float[1024];
        for (var v = 0; v < table.Length; v++)
        {
            table[v] = (float)((2.0 * v - 1023.0) / 1023.0);
        }

        return table;
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
}
