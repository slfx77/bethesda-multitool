using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;

/// <summary>Identifies the unique source shape whose indexed geometry matches an explicitly selected TRI.</summary>
/// <remarks>This proves geometry routing only; it does not establish the actor coefficients' EGM basis.</remarks>
internal sealed class NifPreSkinMorphTarget
{
    private const int MaximumCandidateShapes = 8;

    /// <summary>Retains source identities and exact owner/data indices after a complete topology match.</summary>
    private NifPreSkinMorphTarget(TriGeometryBinding binding, string triSource, int shapeIndex, int fullVertexCount)
    {
        NifSource = binding.GeometrySourceName;
        NifSourceHash = binding.GeometrySourceHash;
        TriSource = triSource;
        TriSourceHash = binding.Document.SourceHash;
        ShapeBlockIndex = shapeIndex;
        DataBlockIndex = binding.GeometryBlockIndex;
        BaseVertexCount = binding.Document.Header.VertexCount;
        FullVertexCount = fullVertexCount;
        Topology = binding.Topology;
    }

    public string NifSource { get; }
    public string NifSourceHash { get; }
    public string TriSource { get; }
    public string TriSourceHash { get; }
    public int ShapeBlockIndex { get; }
    public int DataBlockIndex { get; }
    public int BaseVertexCount { get; }
    public int FullVertexCount { get; }
    public TriTopologyMatch Topology { get; }

    /// <summary>Finds one visible skinned owner matching the selected TRI, without choosing by shape order or counts alone.</summary>
    /// <param name="nifBytes">Stable original little-endian NIF bytes.</param>
    /// <param name="nifSource">The actual resolved NIF source locator.</param>
    /// <param name="triBytes">The explicit complete TRI source bytes.</param>
    /// <param name="triSource">The actual resolved TRI source locator.</param>
    /// <param name="fullVertexCount">The unchanged legacy EGM displacement domain, including the statistical suffix.</param>
    /// <returns>A verified routing target, or null when no unique eligible topology match exists.</returns>
    /// <exception cref="InvalidDataException">The source framing or full domain is invalid.</exception>
    /// <exception cref="NotSupportedException">The source or candidate set exceeds this bounded adapter's profile.</exception>
    internal static NifPreSkinMorphTarget? Find(byte[] nifBytes, string nifSource, byte[] triBytes,
        string triSource, int fullVertexCount)
    {
        if (nifBytes.Length > TriNifBinding.MaximumNifBytes)
        {
            throw new NotSupportedException("Head morph binding exceeds the NIF byte limit.");
        }
        var tri = TriReader.Read(triBytes);
        if (fullVertexCount != tri.Header.VertexCount + tri.Header.StatisticalVertexCount)
        {
            throw new InvalidDataException("Head EGM displacement domain differs from the selected TRI V+K.");
        }
        var nif = NifParser.Parse(nifBytes) ?? throw new InvalidDataException("Head morph source is not a NIF.");
        if (nif.IsBigEndian)
        {
            throw new NotSupportedException("Verified head routing requires original little-endian geometry.");
        }
        var candidates = FindCandidates(nifBytes, nif, tri.Header.VertexCount);
        var reference = new Vector3[fullVertexCount];
        tri.Vertices.Span.CopyTo(reference);
        tri.StatisticalVertices.Span.CopyTo(reference.AsSpan(tri.Header.VertexCount));
        NifPreSkinMorphTarget? result = null;
        foreach (var (shapeIndex, dataIndex) in candidates)
        {
            var binding = TryBind(nifBytes, nifSource, dataIndex, tri, reference);
            if (binding is null)
            {
                continue;
            }
            if (result is not null)
            {
                return null;
            }
            result = new NifPreSkinMorphTarget(binding, triSource, shapeIndex, fullVertexCount);
        }
        return result;
    }

    /// <summary>Restricts proof work to eight visible legacy skinned candidate owners with the exact base count.</summary>
    private static List<(int Shape, int Data)> FindCandidates(byte[] bytes, NifInfo nif, int baseVertexCount)
    {
        var children = new Dictionary<int, List<int>>();
        var geometry = new Dictionary<int, int>();
        var properties = new Dictionary<int, List<int>>();
        var skins = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(bytes, nif, children, geometry, properties, skins);
        var result = new List<(int, int)>();
        foreach (var (shape, data) in geometry)
        {
            if (!skins.ContainsKey(shape) || NifBlockParsers.IsHiddenShape(bytes, nif.Blocks[shape], nif) ||
                nif.Blocks[data].TypeName is not ("NiTriShapeData" or "NiTriStripsData") ||
                NifBlockParsers.ReadVertexCount(bytes, nif.Blocks[data], false, nif.BinaryVersion) != baseVertexCount)
            {
                continue;
            }
            result.Add((shape, data));
            if (result.Count > MaximumCandidateShapes)
            {
                throw new NotSupportedException("Head morph binding exceeds eight eligible shape candidates.");
            }
        }
        return result;
    }

    /// <summary>Uses the existing exact oriented-topology proof; malformed or mismatched candidates do not gain authority.</summary>
    private static TriGeometryBinding? TryBind(byte[] bytes, string source, int dataIndex, TriDocument tri,
        Vector3[] reference)
    {
        try
        {
            return TriNifBinding.Bind(bytes, source, dataIndex, tri, reference);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Rejects stale sources or truncated V+K arrays before either extraction path can apply this target.</summary>
    /// <param name="bytes">The exact source bytes about to be extracted.</param>
    /// <param name="deltas">The complete existing EGM displacement array.</param>
    /// <exception cref="InvalidDataException">The source or displacement domain no longer matches the proof.</exception>
    internal void ValidateSource(byte[] bytes, float[]? deltas)
    {
        if (bytes.Length > TriNifBinding.MaximumNifBytes ||
            deltas is null || deltas.Length != checked(FullVertexCount * 3) ||
            !string.Equals(NifSourceHash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Verified pre-skin morph source or complete displacement domain changed.");
        }
    }

    /// <summary>Matches the exact owner and geometry data; another eligible shape cannot consume its deltas.</summary>
    internal bool Matches(int shapeIndex, int dataIndex) =>
        ShapeBlockIndex == shapeIndex && DataBlockIndex == dataIndex;
}
