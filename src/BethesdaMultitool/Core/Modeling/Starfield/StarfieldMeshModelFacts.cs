using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The reader's checks over a parsed <c>.mesh</c> (cut-2 plan section 3.2) and the facts it records for the
///     diagnostics and native state: the 2-bit W codes of the normals and tangents, the Dec4 zero sentinels, the non-finite
///     UV components and the unused vertices. <see cref="Measure" /> throws for every rule a retail file never breaks and
///     the probe would not admit, naming the field and offset.
/// </summary>
internal sealed class StarfieldMeshModelFacts
{
    /// <summary>The packed Dec4 zero code (511, 511, 511) without its W bits.</summary>
    public const uint SentinelCode = 511u | (511u << 10) | (511u << 20);

    /// <summary>The most LOD lists the reader admits, the probe's bound (<see cref="StarfieldMeshModelProbe.MaximumLods" />).</summary>
    public const int MaximumLods = StarfieldMeshModelProbe.MaximumLods;

    /// <summary>The most weights per vertex the reader admits, the probe's bound.</summary>
    public const int MaximumWeightsPerVertex = StarfieldMeshModelProbe.MaximumWeightsPerVertex;

    private StarfieldMeshModelFacts(int vertexCount)
    {
        VertexCount = vertexCount;
    }

    /// <summary>The vertex count.</summary>
    public int VertexCount { get; }

    /// <summary>The first normal's 2-bit W, or null when the stream stores no normals.</summary>
    public uint? FirstNormalW { get; private init; }

    /// <summary>How many normals carry each 2-bit W code (index = code).</summary>
    public int[] NormalW { get; private init; } = new int[4];

    /// <summary>How many tangents carry each 2-bit W code (index = code).</summary>
    public int[] TangentW { get; private init; } = new int[4];

    /// <summary>How many normals are the Dec4 zero code, by W.</summary>
    public int[] NormalSentinelsByW { get; private init; } = new int[4];

    /// <summary>How many tangents are the Dec4 zero code, by W.</summary>
    public int[] TangentSentinelsByW { get; private init; } = new int[4];

    /// <summary>The non-finite components of UV set 0.</summary>
    public int Uv0NonFinite { get; private init; }

    /// <summary>The non-finite components of UV set 1.</summary>
    public int Uv1NonFinite { get; private init; }

    /// <summary>Vertices no triangle of the main index list references.</summary>
    public int UnusedVertices { get; private init; }

    /// <summary>The largest magnitude among the stored int16 position components.</summary>
    public int QuantizedMaxAbs { get; private init; }

    /// <summary>The Dec4 zero sentinels among normals and tangents together.</summary>
    public int Sentinels => NormalSentinelsByW.Sum() + TangentSentinelsByW.Sum();

    /// <summary>Checks a parsed stream against the reader's rules and measures its facts.</summary>
    /// <param name="mesh">The parse of the whole stream.</param>
    /// <param name="length">The stream's byte length.</param>
    /// <exception cref="InvalidDataException">The stream breaks a rule (see the format metadata's corrupt-input rule).</exception>
    /// <exception cref="NotSupportedException">The stream declares more LOD lists or weights per vertex than the reader admits.</exception>
    public static StarfieldMeshModelFacts Measure(StarfieldMeshFile mesh, int length)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.BytesConsumed != length)
        {
            throw Invalid("end", mesh.BytesConsumed,
                $"{length - mesh.BytesConsumed} byte(s) follow the last section");
        }

        var vertices = mesh.QuantizedPositions.Length / 3;
        if (mesh.IndexCount == 0)
        {
            throw Invalid("indices", Offset(mesh, "indices"), "the stream declares no indices");
        }

        if (mesh.IndexCount % 3 != 0)
        {
            throw Invalid("indices", Offset(mesh, "indices"),
                $"{mesh.IndexCount} indices is not a whole number of triangles");
        }

        RequireIndices(mesh.Indices, vertices, "indices", Offset(mesh, "indices"));
        foreach (var name in new[] { "uv0", "uv1", "colors", "normals", "tangents" })
        {
            var section = Section(mesh, name);
            if (section.Count != 0 && section.Count != vertices)
            {
                throw Invalid(name, section.Offset,
                    $"{section.Count} elements for {vertices} vertices (a stream holds 0 or one per vertex)");
            }
        }

        if (mesh.WeightsPerVertex is < 0 or > MaximumWeightsPerVertex)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The .mesh declares {(uint)mesh.WeightsPerVertex} weights per vertex; the reader admits at most " +
                $"{MaximumWeightsPerVertex} (the retail maximum is 8)."));
        }

        var weights = Section(mesh, "weights");
        if ((long)weights.Count != (long)vertices * mesh.WeightsPerVertex)
        {
            throw Invalid("weights", weights.Offset,
                $"{weights.Count} weight pairs for {vertices} vertices x {mesh.WeightsPerVertex} weights per vertex");
        }

        if (mesh.LodIndexLists.Count > MaximumLods)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The .mesh declares {mesh.LodIndexLists.Count} LOD lists; the reader admits at most {MaximumLods} " +
                $"(the retail maximum is 3)."));
        }

        for (var lod = 0; lod < mesh.LodIndexLists.Count; lod++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"lod:{lod + 1}");
            var list = mesh.LodIndexLists[lod];
            if (list.Length % 3 != 0)
            {
                throw Invalid(name, Offset(mesh, name), $"{list.Length} indices is not a whole number of triangles");
            }

            RequireIndices(list, vertices, name, Offset(mesh, name));
        }

        var tangentW = new int[4];
        var tangentSentinels = new int[4];
        if (mesh.TangentCodes is { } tangents)
        {
            for (var i = 0; i < tangents.Length; i++)
            {
                var w = tangents[i] >> 30;
                if (w is 1 or 2)
                {
                    throw Invalid("tangents", Offset(mesh, "tangents") + 4 + i * 4,
                        $"tangent {i} carries W code {w}; only 0 and 3 are bitangent signs");
                }

                tangentW[w]++;
                if ((tangents[i] & 0x3FFFFFFF) == SentinelCode)
                {
                    tangentSentinels[w]++;
                }
            }
        }

        var normalW = new int[4];
        var normalSentinels = new int[4];
        uint? firstNormalW = null;
        if (mesh.NormalCodes is { Length: > 0 } normals)
        {
            firstNormalW = normals[0] >> 30;
            foreach (var code in normals)
            {
                normalW[code >> 30]++;
                if ((code & 0x3FFFFFFF) == SentinelCode)
                {
                    normalSentinels[code >> 30]++;
                }
            }

            if (firstNormalW == 1 && !mesh.HasMeshletTail)
            {
                throw Invalid("meshlets", mesh.BytesConsumed,
                    "truncated at the meshlet tail: the stream ends where the tail starts while the first normal's W " +
                    "is 1, which on every retail file means the meshlet and cull tail follows");
            }
        }

        var used = new bool[vertices];
        foreach (var index in mesh.Indices)
        {
            used[index] = true;
        }

        var maxAbs = 0;
        foreach (var q in mesh.QuantizedPositions)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs((int)q));
        }

        return new StarfieldMeshModelFacts(vertices)
        {
            FirstNormalW = firstNormalW,
            NormalW = normalW,
            TangentW = tangentW,
            NormalSentinelsByW = normalSentinels,
            TangentSentinelsByW = tangentSentinels,
            Uv0NonFinite = CountNonFinite(mesh.Uv0Bits),
            Uv1NonFinite = CountNonFinite(mesh.Uv1Bits),
            UnusedVertices = used.Count(static u => !u),
            QuantizedMaxAbs = maxAbs
        };
    }

    /// <summary>The named section of a parse (every parse records each fixed section name exactly once).</summary>
    public static StarfieldMeshSection Section(StarfieldMeshFile mesh, string name)
    {
        return mesh.Sections.First(section => string.Equals(section.Name, name, StringComparison.Ordinal));
    }

    /// <summary>The offset of the named section.</summary>
    public static int Offset(StarfieldMeshFile mesh, string name)
    {
        return Section(mesh, name).Offset;
    }

    /// <summary>Whether a half's exponent bits are all set (an infinity or a NaN).</summary>
    public static bool IsNonFiniteHalf(ushort bits)
    {
        return ((bits >> 10) & 0x1F) == 0x1F;
    }

    private static int CountNonFinite(ushort[]? bits)
    {
        return bits?.Count(IsNonFiniteHalf) ?? 0;
    }

    private static void RequireIndices(ushort[] indices, int vertices, string field, int offset)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= vertices)
            {
                throw Invalid(field, offset + 4 + i * 2,
                    $"index {i} is {indices[i]}, not below the vertex count {vertices}");
            }
        }
    }

    private static InvalidDataException Invalid(string field, int offset, string reason)
    {
        return new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"Starfield .mesh {field} at 0x{offset:X}: {reason}."));
    }
}
