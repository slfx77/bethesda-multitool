using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelTextureSource" /> produced for one read.</summary>
internal sealed class NifModelTextureResult
{
    /// <summary>Creates a result.</summary>
    public NifModelTextureResult(
        IReadOnlyList<SceneImage> images,
        IReadOnlyList<SceneNativeState> nativeRows,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions,
        IReadOnlyDictionary<int, int> imageByFedBlock,
        IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        Images = images;
        NativeRows = nativeRows;
        Dispositions = dispositions;
        ImageByFedBlock = imageByFedBlock;
        Diagnostics = diagnostics;
    }

    /// <summary>The document images, in first-binding order.</summary>
    public IReadOnlyList<SceneImage> Images { get; }

    /// <summary>One <c>bmt.nif.texture</c> row per image.</summary>
    public IReadOnlyList<SceneNativeState> NativeRows { get; }

    /// <summary>Coverage decisions for the NiSourceTexture blocks the source resolved.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>For each NiSourceTexture block, the image it fed (native-state targeting).</summary>
    public IReadOnlyDictionary<int, int> ImageByFedBlock { get; }

    /// <summary>Missing, ambiguous, gate and header diagnostics, bounded per code.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
