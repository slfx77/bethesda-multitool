using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelGeometryReader" /> produced for one file.</summary>
internal sealed class NifModelGeometryResult
{
    /// <summary>Creates a result.</summary>
    public NifModelGeometryResult(
        IReadOnlyList<SceneMesh> meshes,
        IReadOnlyDictionary<int, int> meshByGeometryBlock,
        IReadOnlyDictionary<int, int> meshByNode,
        IReadOnlyDictionary<int, int> meshByFedBlock,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions,
        IReadOnlyList<NifModelPrimitiveRow> primitiveRows,
        IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        Meshes = meshes;
        MeshByGeometryBlock = meshByGeometryBlock;
        MeshByNode = meshByNode;
        MeshByFedBlock = meshByFedBlock;
        Dispositions = dispositions;
        PrimitiveRows = primitiveRows;
        Diagnostics = diagnostics;
    }

    /// <summary>
    ///     The document meshes: one per placed geometry block and distinct material among its placements (slice 5), in
    ///     block order, each with one primitive.
    /// </summary>
    public IReadOnlyList<SceneMesh> Meshes { get; }

    /// <summary>For each placed geometry block that yielded a primitive, its first mesh.</summary>
    public IReadOnlyDictionary<int, int> MeshByGeometryBlock { get; }

    /// <summary>For each node occurrence of a geometry block that yielded a primitive, the mesh it places.</summary>
    public IReadOnlyDictionary<int, int> MeshByNode { get; }

    /// <summary>For each data or morph data block that fed a mesh, the first mesh it fed (native-state targeting).</summary>
    public IReadOnlyDictionary<int, int> MeshByFedBlock { get; }

    /// <summary>Coverage decisions for every geometry, data and morph data block the reader visited.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>One native row per emitted primitive, in mesh order.</summary>
    public IReadOnlyList<NifModelPrimitiveRow> PrimitiveRows { get; }

    /// <summary>Document diagnostics (non-finite colors or tangents, morph issues), bounded per code.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
