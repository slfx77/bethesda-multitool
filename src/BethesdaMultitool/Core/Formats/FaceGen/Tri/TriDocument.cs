using System.Collections.ObjectModel;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.FaceGen.Tri;

/// <summary>The two distinct morph families stored in an FRTRI003 document.</summary>
public enum TriMorphKind
{
    Differential,
    Statistical
}

/// <summary>Addresses one morph by its source family and source-order index, without guessing aliases.</summary>
public readonly record struct TriMorphReference(TriMorphKind Kind, int Index);

/// <summary>An explicitly selected morph and finite diagnostic weight; no LIP mapping is implied.</summary>
public readonly record struct TriMorphWeight(TriMorphReference Morph, float Value);

/// <summary>Retains a label's exact bytes and explicit decoding convention.</summary>
public sealed class TriLabel
{
    /// <summary>Owns a validated byte string and its decoded display value.</summary>
    internal TriLabel(byte[] bytes, string text, int codeUnitWidth)
    {
        Bytes = bytes;
        Text = text;
        CodeUnitWidth = codeUnitWidth;
    }

    /// <summary>Exact source bytes, including a morph label's required terminal null.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Byte labels use a lossless Latin-1 display convention; wide surface labels use UTF-16LE.</summary>
    public string Text { get; }

    /// <summary>Stored character width, one byte or two bytes.</summary>
    public int CodeUnitWidth { get; }
}

/// <summary>A source vertex label; its index addresses the base vertex domain.</summary>
public sealed record TriVertexLabel(int VertexIndex, TriLabel Label);

/// <summary>Retains one surface label's source integer and three finite coordinates without inferring their application.</summary>
public sealed record TriSurfaceLabel(int SurfaceIndex, Vector3 Coordinates, TriLabel Label);

/// <summary>A labelled dense signed-int16 delta array with its original scale.</summary>
public sealed class TriDifferentialMorph
{
    /// <summary>Owns one validated packed delta array.</summary>
    internal TriDifferentialMorph(TriLabel label, float scale, short[] packedDeltas)
    {
        Label = label;
        Scale = scale;
        PackedDeltas = packedDeltas;
    }

    /// <summary>The exact source label and its display value.</summary>
    public TriLabel Label { get; }

    /// <summary>Multiplier converting every stored signed short into a coordinate delta.</summary>
    public float Scale { get; }

    /// <summary>Dense X/Y/Z triples in base-vertex order.</summary>
    public ReadOnlyMemory<short> PackedDeltas { get; }

    /// <summary>Returns one decoded coordinate delta without changing the stored representation.</summary>
    public Vector3 GetDelta(int vertexIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertexIndex, PackedDeltas.Length / 3);
        var offset = vertexIndex * 3;
        var values = PackedDeltas.Span;
        return new Vector3(values[offset] * Scale, values[offset + 1] * Scale, values[offset + 2] * Scale);
    }
}

/// <summary>A labelled list of base-vertex indices addressing consecutive target vertices in the statistical domain.</summary>
public sealed class TriStatisticalMorph
{
    /// <summary>Owns validated affected indices and references its matching source target slice.</summary>
    internal TriStatisticalMorph(TriLabel label, int[] vertexIndices, int firstTargetVertex,
        ReadOnlyMemory<Vector3> targets)
    {
        Label = label;
        VertexIndices = vertexIndices;
        FirstTargetVertex = firstTargetVertex;
        Targets = targets;
    }

    /// <summary>The exact source label and its display value.</summary>
    public TriLabel Label { get; }

    /// <summary>The affected base-vertex indices in the same order as the target slice.</summary>
    public ReadOnlyMemory<int> VertexIndices { get; }

    /// <summary>The first target's index in the combined V+K shape domain.</summary>
    public int FirstTargetVertex { get; }

    /// <summary>Original statistical target coordinates; actor-specific face shaping has not been applied.</summary>
    public ReadOnlyMemory<Vector3> Targets { get; }
}

/// <summary>Retains every FRTRI003 header field without provisional section names.</summary>
public sealed class TriHeader
{
    /// <summary>Owns validated fixed header words and the uninterpreted reserved bytes.</summary>
    internal TriHeader(int[] counts, uint extensionFlags, byte[] reserved)
    {
        VertexCount = counts[0];
        TriangleCount = counts[1];
        QuadCount = counts[2];
        VertexLabelCount = counts[3];
        SurfaceLabelCount = counts[4];
        TextureCoordinateCount = counts[5];
        DifferentialMorphCount = counts[6];
        StatisticalMorphCount = counts[7];
        StatisticalVertexCount = counts[8];
        ExtensionFlags = extensionFlags;
        Reserved = reserved;
    }

    /// <summary>Number of base vertices V.</summary>
    public int VertexCount { get; }
    /// <summary>Number of triangle facets T.</summary>
    public int TriangleCount { get; }
    /// <summary>Number of quadrilateral facets Q.</summary>
    public int QuadCount { get; }
    /// <summary>Number of labelled vertices LV.</summary>
    public int VertexLabelCount { get; }
    /// <summary>Number of labelled surface points LS.</summary>
    public int SurfaceLabelCount { get; }
    /// <summary>Header count X; zero selects per-vertex UVs when UVs are present.</summary>
    public int TextureCoordinateCount { get; }
    /// <summary>Source extension flags: bit zero enables UVs; bit one makes surface labels wide.</summary>
    public uint ExtensionFlags { get; }
    /// <summary>Number of labelled differential records Md.</summary>
    public int DifferentialMorphCount { get; }
    /// <summary>Number of labelled statistical records Ms.</summary>
    public int StatisticalMorphCount { get; }
    /// <summary>Total statistical target vertices K, equal to the sum of statistical affected-index counts.</summary>
    public int StatisticalVertexCount { get; }
    /// <summary>All sixteen reserved header bytes, retained without assigning semantics.</summary>
    public ReadOnlyMemory<byte> Reserved { get; }
}

/// <summary>Owns the parser's validated arrays until they are exposed through read-only document views.</summary>
internal sealed class TriPayload
{
    internal required Vector3[] Vertices { get; init; }
    internal required Vector3[] StatisticalVertices { get; init; }
    internal required int[] Triangles { get; init; }
    internal required int[] Quads { get; init; }
    internal required TriVertexLabel[] VertexLabels { get; init; }
    internal required TriSurfaceLabel[] SurfaceLabels { get; init; }
    internal required Vector2[] TextureCoordinates { get; init; }
    internal required int[] TriangleTextureIndices { get; init; }
    internal required int[] QuadTextureIndices { get; init; }
    internal required TriDifferentialMorph[] DifferentialMorphs { get; init; }
    internal required TriStatisticalMorph[] StatisticalMorphs { get; init; }
}

/// <summary>A complete bounded FRTRI003 source document, including both morph families and exact input identity.</summary>
public sealed class TriDocument
{
    /// <summary>Takes ownership of parser-created arrays and publishes read-only views without flattening morph families.</summary>
    internal TriDocument(TriHeader header, TriPayload payload, string sourceHash, int encodedSize)
    {
        Header = header;
        Vertices = payload.Vertices;
        StatisticalVertices = payload.StatisticalVertices;
        Triangles = payload.Triangles;
        Quads = payload.Quads;
        VertexLabels = Array.AsReadOnly(payload.VertexLabels);
        SurfaceLabels = Array.AsReadOnly(payload.SurfaceLabels);
        TextureCoordinates = payload.TextureCoordinates;
        TriangleTextureIndices = payload.TriangleTextureIndices;
        QuadTextureIndices = payload.QuadTextureIndices;
        DifferentialMorphs = Array.AsReadOnly(payload.DifferentialMorphs);
        StatisticalMorphs = Array.AsReadOnly(payload.StatisticalMorphs);
        SourceHash = sourceHash;
        EncodedSize = encodedSize;
    }

    /// <summary>Validated header values and original reserved bytes.</summary>
    public TriHeader Header { get; }
    /// <summary>Base coordinates in source vertex order; these need not equal an associated NIF's neutral coordinates.</summary>
    public ReadOnlyMemory<Vector3> Vertices { get; }
    /// <summary>The K target coordinates stored immediately after the V base coordinates.</summary>
    public ReadOnlyMemory<Vector3> StatisticalVertices { get; }
    /// <summary>Triangle vertex indices in original facet order.</summary>
    public ReadOnlyMemory<int> Triangles { get; }
    /// <summary>Quad vertex indices in original facet order.</summary>
    public ReadOnlyMemory<int> Quads { get; }
    /// <summary>Source-order labelled vertices.</summary>
    public ReadOnlyCollection<TriVertexLabel> VertexLabels { get; }
    /// <summary>Source-order labelled surface records.</summary>
    public ReadOnlyCollection<TriSurfaceLabel> SurfaceLabels { get; }
    /// <summary>Original UV coordinates; no V flip or vertex splitting is applied.</summary>
    public ReadOnlyMemory<Vector2> TextureCoordinates { get; }
    /// <summary>Triangle UV indices for per-facet UVs, otherwise empty.</summary>
    public ReadOnlyMemory<int> TriangleTextureIndices { get; }
    /// <summary>Quad UV indices for per-facet UVs, otherwise empty.</summary>
    public ReadOnlyMemory<int> QuadTextureIndices { get; }
    /// <summary>Dense delta morphs in source order, including duplicate labels when present.</summary>
    public ReadOnlyCollection<TriDifferentialMorph> DifferentialMorphs { get; }
    /// <summary>Indexed target morphs in source order, including duplicate labels when present.</summary>
    public ReadOnlyCollection<TriStatisticalMorph> StatisticalMorphs { get; }
    /// <summary>Uppercase SHA-256 of the complete original file bytes.</summary>
    public string SourceHash { get; }
    /// <summary>The exactly consumed encoded byte count.</summary>
    public int EncodedSize { get; }

    /// <summary>Resolves a unique exact source label; missing and duplicate identities fail instead of selecting a candidate.</summary>
    public TriMorphReference ResolveMorph(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        TriMorphReference? match = null;
        for (var index = 0; index < DifferentialMorphs.Count; index++)
        {
            if (DifferentialMorphs[index].Label.Text != name) continue;
            if (match.HasValue) throw new InvalidDataException($"Ambiguous TRI morph label: {name}");
            match = new TriMorphReference(TriMorphKind.Differential, index);
        }
        for (var index = 0; index < StatisticalMorphs.Count; index++)
        {
            if (StatisticalMorphs[index].Label.Text != name) continue;
            if (match.HasValue) throw new InvalidDataException($"Ambiguous TRI morph label: {name}");
            match = new TriMorphReference(TriMorphKind.Statistical, index);
        }
        return match ?? throw new KeyNotFoundException($"TRI morph label was not found: {name}");
    }
}
