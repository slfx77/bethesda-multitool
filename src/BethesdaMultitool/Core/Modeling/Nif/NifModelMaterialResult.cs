using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelMaterialReader" /> produced for one read.</summary>
internal sealed class NifModelMaterialResult
{
    /// <summary>Creates a result.</summary>
    public NifModelMaterialResult(
        IReadOnlyList<SceneMaterial> materials,
        IReadOnlyList<SceneSampler> samplers,
        IReadOnlyList<SceneNativeState> nativeRows,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions,
        IReadOnlyDictionary<int, int> materialByFedBlock,
        IReadOnlyList<SceneDiagnostic> diagnostics,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? materialsByFedBlock = null,
        IReadOnlyList<IReadOnlyList<NifModelLayerOrigin>>? layerOrigins = null)
    {
        Materials = materials;
        Samplers = samplers;
        NativeRows = nativeRows;
        Dispositions = dispositions;
        MaterialByFedBlock = materialByFedBlock;
        Diagnostics = diagnostics;
        MaterialsByFedBlock = materialsByFedBlock ?? new Dictionary<int, IReadOnlyList<int>>();
        LayerOrigins = layerOrigins ?? [];
    }

    /// <summary>The document materials, one per distinct effective-property key, in first-use order.</summary>
    public IReadOnlyList<SceneMaterial> Materials { get; }

    /// <summary>The document samplers, deduplicated, in first-use order.</summary>
    public IReadOnlyList<SceneSampler> Samplers { get; }

    /// <summary>One <c>bmt.nif.material</c> row per material.</summary>
    public IReadOnlyList<SceneNativeState> NativeRows { get; }

    /// <summary>Coverage decisions for the property and texture-set blocks the reader typed.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>For each property or texture-set block, the first material it fed (native-state targeting).</summary>
    public IReadOnlyDictionary<int, int> MaterialByFedBlock { get; }

    /// <summary>
    ///     For each property or texture-set block, EVERY material it fed, in first-use order (slice 14: a property
    ///     controller on a shared block drives every material the block feeds, one track each).
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> MaterialsByFedBlock { get; }

    /// <summary>
    ///     For each material, where each of its <see cref="SceneMaterial.Layers" /> came from, in layer order (slice 14:
    ///     the texture-slot to layer-ordinal map).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<NifModelLayerOrigin>> LayerOrigins { get; }

    /// <summary>Material diagnostics, bounded per code.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
