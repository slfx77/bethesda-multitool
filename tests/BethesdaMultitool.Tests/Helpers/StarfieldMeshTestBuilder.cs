namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     An independent writer of the Starfield <c>.mesh</c> layout (cut-2 plan section 0.2; the writer of
///     <c>tools/scripts/gate2/starfield_mesh_probe.py</c> restated), for versions 0 to 2, with or without the meshlet and
///     cull tail, LOD lists, weights, both UV sets, colors, sentinel codes and non-finite halves. It shares no code with
///     BMT's <c>StarfieldMeshFile</c>, and <see cref="Build" /> reports where it wrote each section, so a test can compare
///     the decoder's section walk against the writer's own bookkeeping.
/// </summary>
/// <remarks>
///     Every array is written exactly as given (a count dword, then the elements), so a test can write deliberately
///     inconsistent streams: a count that disagrees with the vertex count, an index at the vertex count, an index count
///     that is not a multiple of 3. <see cref="IndexCountOverride" /> writes a declared index count different from the
///     number of indices written.
/// </remarks>
internal sealed class StarfieldMeshTestBuilder
{
    /// <summary>The half bits of 0.0.</summary>
    public const ushort HalfZero = 0x0000;

    /// <summary>The half bits of 1.0.</summary>
    public const ushort HalfOne = 0x3C00;

    /// <summary>The half bits of positive infinity.</summary>
    public const ushort HalfInfinity = 0x7C00;

    /// <summary>The container version dword.</summary>
    public uint Version { get; set; } = 2;

    /// <summary>The main index list.</summary>
    public ushort[] Indices { get; set; } = [0, 1, 2, 0, 2, 3];

    /// <summary>A declared index count written instead of <c>Indices.Length</c>, or null.</summary>
    public uint? IndexCountOverride { get; set; }

    /// <summary>The file-global position scale.</summary>
    public float Scale { get; set; } = 4f;

    /// <summary>The weights-per-vertex dword.</summary>
    public uint WeightsPerVertex { get; set; }

    /// <summary>The stored int16 positions.</summary>
    public (short X, short Y, short Z)[] Positions { get; set; } = [];

    /// <summary>UV set 0 as half bits, or null for an empty set.</summary>
    public (ushort U, ushort V)[]? Uv0 { get; set; }

    /// <summary>UV set 1 as half bits, or null for an empty set.</summary>
    public (ushort U, ushort V)[]? Uv1 { get; set; }

    /// <summary>Vertex colors exactly as stored: B, G, R, A per vertex, or null for none.</summary>
    public (byte B, byte G, byte R, byte A)[]? Colors { get; set; }

    /// <summary>Packed 10/10/10/2 normal words, or null for none.</summary>
    public uint[]? Normals { get; set; }

    /// <summary>Packed 10/10/10/2 tangent words, or null for none.</summary>
    public uint[]? Tangents { get; set; }

    /// <summary>Skin weight pairs (bone, weight), vertex-major then slot, or null for none.</summary>
    public (ushort Bone, ushort Weight)[]? Weights { get; set; }

    /// <summary>LOD index lists (written only for version 1 and above).</summary>
    public ushort[][] Lods { get; set; } = [];

    /// <summary>Whether the meshlet and cull tail is written.</summary>
    public bool Tail { get; set; } = true;

    /// <summary>Meshlet records (vertex count, vertex offset, triangle count, triangle offset).</summary>
    public (uint VertexCount, uint VertexOffset, uint TriangleCount, uint TriangleOffset)[] Meshlets { get; set; } = [];

    /// <summary>Cull records, six floats each (center, extent).</summary>
    public float[][] Cull { get; set; } = [];

    /// <summary>Bytes appended after the last section.</summary>
    public byte[] Trailing { get; set; } = [];

    /// <summary>Packs a 10/10/10/2 word from three 10-bit channel codes and a 2-bit W.</summary>
    public static uint PackDec4(uint x, uint y, uint z, uint w)
    {
        return (x & 0x3FF) | ((y & 0x3FF) << 10) | ((z & 0x3FF) << 20) | ((w & 3) << 30);
    }

    /// <summary>
    ///     The standard fixture: a planar quad in the XY plane at scale 4 whose components (-10000 and 20000) are values
    ///     where the legacy two-rounding position route differs from the correctly rounded one; UV0 (0,0), (1,0), (1,1),
    ///     (0,1) so U runs along +X and V along +Y; normals +Z (codes 512, 512, 1023), tangents +X (1023, 512, 512) with
    ///     W 3; two triangles counter-clockwise about +Z; one meshlet and one cull record (exactly representable center
    ///     and extent, so a second writer produces the same bits) when the tail is written. The
    ///     normals' W follows the retail tail rule: 1 with the tail, 0 without it.
    /// </summary>
    public static StarfieldMeshTestBuilder Quad(bool tail = true, uint version = 2)
    {
        return new StarfieldMeshTestBuilder
        {
            Version = version,
            Indices = [0, 1, 2, 0, 2, 3],
            Scale = 4f,
            Positions = [(-10000, -10000, 0), (20000, -10000, 0), (20000, 20000, 0), (-10000, 20000, 0)],
            Uv0 = [(HalfZero, HalfZero), (HalfOne, HalfZero), (HalfOne, HalfOne), (HalfZero, HalfOne)],
            Normals = [.. Enumerable.Repeat(PackDec4(512, 512, 1023, tail ? 1u : 0u), 4)],
            Tangents = [.. Enumerable.Repeat(PackDec4(1023, 512, 512, 3), 4)],
            Tail = tail,
            Meshlets = [(4, 0, 2, 0)],
            Cull = [[0.5f, 0.5f, 0f, 1.75f, 1.75f, 0f]]
        };
    }

    /// <summary>Writes the stream.</summary>
    public byte[] Build()
    {
        return BuildWithLayout().Bytes;
    }

    /// <summary>
    ///     Writes the stream and records each section it wrote: name, offset, length (count dword included) and count,
    ///     in the names <c>StarfieldMeshFile.Sections</c> uses (<c>version</c>, <c>indices</c>, <c>scale</c>,
    ///     <c>weightsPerVertex</c>, <c>positions</c>, <c>uv0</c>, <c>uv1</c>, <c>colors</c>, <c>normals</c>,
    ///     <c>tangents</c>, <c>weights</c>, <c>lods</c>, <c>lod:k</c>, <c>meshlets</c>, <c>cull</c>).
    /// </summary>
    public (byte[] Bytes, IReadOnlyList<(string Name, int Offset, int Length, uint Count)> Sections) BuildWithLayout()
    {
        var w = new List<byte>();
        var sections = new List<(string Name, int Offset, int Length, uint Count)>();

        void Section(string name, int offset, uint count)
        {
            sections.Add((name, offset, w.Count - offset, count));
        }

        var start = w.Count;
        U32(w, Version);
        Section("version", start, 1);

        start = w.Count;
        U32(w, IndexCountOverride ?? (uint)Indices.Length);
        foreach (var index in Indices)
        {
            U16(w, index);
        }

        Section("indices", start, IndexCountOverride ?? (uint)Indices.Length);

        start = w.Count;
        w.AddRange(BitConverter.GetBytes(Scale));
        Section("scale", start, 1);

        start = w.Count;
        U32(w, WeightsPerVertex);
        Section("weightsPerVertex", start, 1);

        start = w.Count;
        U32(w, (uint)Positions.Length);
        foreach (var (x, y, z) in Positions)
        {
            U16(w, (ushort)x);
            U16(w, (ushort)y);
            U16(w, (ushort)z);
        }

        Section("positions", start, (uint)Positions.Length);

        foreach (var (name, set) in new[] { ("uv0", Uv0), ("uv1", Uv1) })
        {
            start = w.Count;
            U32(w, (uint)(set?.Length ?? 0));
            foreach (var (u, v) in set ?? [])
            {
                U16(w, u);
                U16(w, v);
            }

            Section(name, start, (uint)(set?.Length ?? 0));
        }

        start = w.Count;
        U32(w, (uint)(Colors?.Length ?? 0));
        foreach (var (b, g, r, a) in Colors ?? [])
        {
            w.Add(b);
            w.Add(g);
            w.Add(r);
            w.Add(a);
        }

        Section("colors", start, (uint)(Colors?.Length ?? 0));

        foreach (var (name, codes) in new[] { ("normals", Normals), ("tangents", Tangents) })
        {
            start = w.Count;
            U32(w, (uint)(codes?.Length ?? 0));
            foreach (var code in codes ?? [])
            {
                U32(w, code);
            }

            Section(name, start, (uint)(codes?.Length ?? 0));
        }

        start = w.Count;
        U32(w, (uint)(Weights?.Length ?? 0));
        foreach (var (bone, weight) in Weights ?? [])
        {
            U16(w, bone);
            U16(w, weight);
        }

        Section("weights", start, (uint)(Weights?.Length ?? 0));

        if (Version != 0)
        {
            var lodsStart = w.Count;
            U32(w, (uint)Lods.Length);
            var lodSections = new List<(string Name, int Offset, int Length, uint Count)>();
            for (var lod = 0; lod < Lods.Length; lod++)
            {
                var lodStart = w.Count;
                U32(w, (uint)Lods[lod].Length);
                foreach (var index in Lods[lod])
                {
                    U16(w, index);
                }

                var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"lod:{lod + 1}");
                lodSections.Add((name, lodStart, w.Count - lodStart, (uint)Lods[lod].Length));
            }

            sections.Add(("lods", lodsStart, w.Count - lodsStart, (uint)Lods.Length));
            sections.AddRange(lodSections);
        }

        if (Tail)
        {
            start = w.Count;
            U32(w, (uint)Meshlets.Length);
            foreach (var (vertexCount, vertexOffset, triangleCount, triangleOffset) in Meshlets)
            {
                U32(w, vertexCount);
                U32(w, vertexOffset);
                U32(w, triangleCount);
                U32(w, triangleOffset);
            }

            Section("meshlets", start, (uint)Meshlets.Length);

            start = w.Count;
            U32(w, (uint)Cull.Length);
            foreach (var record in Cull)
            {
                foreach (var value in record)
                {
                    w.AddRange(BitConverter.GetBytes(value));
                }
            }

            Section("cull", start, (uint)Cull.Length);
        }

        w.AddRange(Trailing);
        return ([.. w], sections);
    }

    /// <summary>The offset where the meshlet tail starts (the end of the LOD section, or of the weights for version 0).</summary>
    public int TailOffset()
    {
        var sections = BuildWithLayout().Sections;
        var last = sections.Last(s => s.Name is "weights" or "lods" || s.Name.StartsWith("lod:", StringComparison.Ordinal));
        return last.Offset + last.Length;
    }

    private static void U32(List<byte> w, uint value)
    {
        w.AddRange(BitConverter.GetBytes(value));
    }

    private static void U16(List<byte> w, ushort value)
    {
        w.AddRange(BitConverter.GetBytes(value));
    }
}
