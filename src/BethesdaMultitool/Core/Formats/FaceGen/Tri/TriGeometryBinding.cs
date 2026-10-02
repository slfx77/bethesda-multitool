using System.Numerics;

namespace BethesdaMultitool.Core.Formats.FaceGen.Tri;

/// <summary>Records exact indexed-triangle comparison, including ignored repeated-index degenerate facets.</summary>
public sealed record TriTopologyMatch(int GeometryFacets, int TriFacets, int GeometryDegenerates, int TriDegenerates);

/// <summary>Owns one source geometry's neutral positions and explicit V+K statistical reference domain.</summary>
/// <remarks>This diagnostic evaluator does not rebuild normals, skin geometry, select LIP aliases or align audio.</remarks>
public sealed class TriGeometryBinding
{
    private readonly Vector3[] _rest;
    private readonly Vector3[] _referenceDomain;
    private readonly int[] _destinationToSource;

    /// <summary>Stores validated arrays, preserving source identity and neutral state for every evaluation.</summary>
    private TriGeometryBinding(TriDocument document, string geometrySourceName, string geometrySourceHash,
        int geometryBlockIndex, Vector3[] rest, Vector3[] referenceDomain, int[] destinationToSource,
        TriTopologyMatch topology)
    {
        Document = document;
        GeometrySourceName = geometrySourceName;
        GeometrySourceHash = geometrySourceHash;
        GeometryBlockIndex = geometryBlockIndex;
        _rest = rest;
        _referenceDomain = referenceDomain;
        _destinationToSource = destinationToSource;
        Topology = topology;
    }

    /// <summary>The source TRI document whose family/index identities this binding accepts.</summary>
    public TriDocument Document { get; }
    /// <summary>The caller's explicit source locator, never inferred from matching names.</summary>
    public string GeometrySourceName { get; }
    /// <summary>SHA-256 computed over the complete source NIF bytes by the binding adapter.</summary>
    public string GeometrySourceHash { get; }
    /// <summary>The explicitly selected source geometry-data block.</summary>
    public int GeometryBlockIndex { get; }
    /// <summary>Validated topology accounting; cyclic corner rotation and facet order do not change indexed geometry.</summary>
    public TriTopologyMatch Topology { get; }
    /// <summary>Number of destination vertices, including any explicit duplicate destinations.</summary>
    public int DestinationVertexCount => _destinationToSource.Length;

    /// <summary>Creates a strict binding for an adapter-extracted geometry; caller arrays are copied before publication.</summary>
    internal static TriGeometryBinding Create(TriDocument document, string sourceName, string sourceHash, int blockIndex,
        ReadOnlySpan<Vector3> rest, ReadOnlySpan<int> triangles, ReadOnlySpan<Vector3> referenceDomain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        cancellationToken.ThrowIfCancellationRequested();
        var count = document.Header.VertexCount;
        if (rest.Length != count || referenceDomain.Length != count + document.Header.StatisticalVertexCount)
        {
            throw new InvalidDataException("TRI binding requires exact geometry V and explicit statistical reference V+K domains.");
        }
        if (!document.Quads.IsEmpty)
        {
            throw new NotSupportedException("NIF TRI binding currently requires triangle-only source topology; quads remain inspectable.");
        }
        ValidateFinite(rest, cancellationToken);
        ValidateFinite(referenceDomain, cancellationToken);
        var geometryKeys = FacetKeys(triangles, count, cancellationToken, out var geometryDegenerates);
        var triKeys = FacetKeys(document.Triangles.Span, count, cancellationToken, out var triDegenerates);
        if (!geometryKeys.AsSpan().SequenceEqual(triKeys))
        {
            throw new InvalidDataException("TRI and selected geometry do not have identical indexed, oriented triangle topology.");
        }
        var destinations = new int[count];
        for (var index = 0; index < count; index++)
        {
            destinations[index] = index;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new TriGeometryBinding(document, sourceName, sourceHash, blockIndex, rest.ToArray(), referenceDomain.ToArray(),
            destinations, new TriTopologyMatch(triangles.Length / 3, document.Header.TriangleCount, geometryDegenerates, triDegenerates));
    }

    /// <summary>Creates a distinct destination layout with explicit duplicate fan-out while retaining every source vertex.</summary>
    public TriGeometryBinding WithDestinationMap(ReadOnlySpan<int> destinationToSource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destinationToSource.Length > TriReader.MaximumVertices * 4)
        {
            throw new NotSupportedException("TRI destination layout exceeds the 2,000,000-vertex limit.");
        }
        var seen = new bool[_rest.Length];
        foreach (var source in destinationToSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)source >= (uint)_rest.Length)
            {
                throw new InvalidDataException("TRI destination map references an invalid source vertex.");
            }
            seen[source] = true;
        }
        if (seen.Contains(false))
        {
            throw new InvalidDataException("TRI destination map omits a source vertex.");
        }
        return new TriGeometryBinding(Document, GeometrySourceName, GeometrySourceHash, GeometryBlockIndex,
            _rest, _referenceDomain, destinationToSource.ToArray(), Topology);
    }

    /// <summary>Evaluates a fresh neutral pose using explicitly selected finite diagnostic weights, including signed weights.</summary>
    /// <remarks>Weights are not the engine's admission policy. Statistical deltas use target minus base in the explicit reference domain.</remarks>
    public Vector3[] Evaluate(ReadOnlySpan<TriMorphWeight> weights, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (weights.Length > Document.DifferentialMorphs.Count + Document.StatisticalMorphs.Count)
        {
            throw new ArgumentException("More weights than unique source morph identities.", nameof(weights));
        }
        var selected = new HashSet<TriMorphReference>();
        var result = (Vector3[])_rest.Clone();
        foreach (var weight in weights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(weight.Value) || !selected.Add(weight.Morph))
            {
                throw new ArgumentException("TRI weights must be finite and source morph identities must be unique.", nameof(weights));
            }
            var index = weight.Morph.Index;
            if (weight.Morph.Kind == TriMorphKind.Differential && (uint)index < (uint)Document.DifferentialMorphs.Count)
            {
                var morph = Document.DifferentialMorphs[index];
                for (var vertex = 0; vertex < result.Length; vertex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result[vertex] += weight.Value * morph.GetDelta(vertex);
                }
            }
            else if (weight.Morph.Kind == TriMorphKind.Statistical && (uint)index < (uint)Document.StatisticalMorphs.Count)
            {
                var morph = Document.StatisticalMorphs[index];
                var indices = morph.VertexIndices.Span;
                for (var affected = 0; affected < indices.Length; affected++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var vertex = indices[affected];
                    var delta = _referenceDomain[morph.FirstTargetVertex + affected] - _referenceDomain[vertex];
                    result[vertex] += weight.Value * delta;
                }
            }
            else
            {
                throw new ArgumentException("TRI weight references an unknown source morph family or index.", nameof(weights));
            }
        }
        ValidateFinite(result, cancellationToken);
        var destination = new Vector3[_destinationToSource.Length];
        for (var index = 0; index < destination.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination[index] = result[_destinationToSource[index]];
        }
        return destination;
    }

    /// <summary>Requires finite coordinates before they can become persistent neutral state or a returned pose.</summary>
    private static void ValidateFinite(ReadOnlySpan<Vector3> values, CancellationToken token)
    {
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw new InvalidDataException("TRI geometry/reference/evaluated domain contains a non-finite coordinate.");
            }
        }
    }

    /// <summary>Builds sorted oriented facet keys, preserving duplicate multiplicity and excluding only repeated-index degenerates.</summary>
    private static (int A, int B, int C)[] FacetKeys(ReadOnlySpan<int> triangles, int vertexCount,
        CancellationToken token, out int degenerates)
    {
        if (triangles.Length % 3 != 0)
        {
            throw new InvalidDataException("Geometry triangle index count is not a multiple of three.");
        }
        var keys = new List<(int A, int B, int C)>(triangles.Length / 3);
        degenerates = 0;
        for (var offset = 0; offset < triangles.Length; offset += 3)
        {
            token.ThrowIfCancellationRequested();
            var a = triangles[offset];
            var b = triangles[offset + 1];
            var c = triangles[offset + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
            {
                throw new InvalidDataException("Geometry facet has an out-of-range vertex index.");
            }
            if (a == b || b == c || c == a)
            {
                degenerates++;
                continue;
            }
            if (a < b && a < c)
            {
                keys.Add((a, b, c));
            }
            else
            {
                keys.Add(b < c ? (b, c, a) : (c, a, b));
            }
        }
        var result = keys.ToArray();
        Array.Sort(result);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
