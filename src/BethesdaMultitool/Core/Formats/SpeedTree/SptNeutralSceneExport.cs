using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>Routes a parsed SpeedTree model to the shared neutral scene through the existing NIF export path.</summary>
/// <remarks>
///     <para>
///         SpeedTree needs no adapter of its own. <see cref="SptGeometryBuilder" /> already produces a
///         <see cref="NifRenderableModel" /> and <c>NifExportSceneBuilder.BuildRenderableModel</c> already turns
///         that into the same <c>GlbScene</c> the NIF path uses, so the neutral snapshot is composition rather
///         than new conversion. Writing a second adapter would duplicate the geometry contract and let the two
///         drift.
///     </para>
///     <para>
///         What SpeedTree does add is vegetation behaviour the neutral model cannot express — billboard
///         orientation, wind-rig speeds and level-of-detail selection are decided per frame or per draw, not
///         stored in a scene. Those decline inside <c>NifNeutralSceneAdapter</c> rather than being approximated,
///         because a still approximation that makes the numbers look complete is a placeholder.
///     </para>
/// </remarks>
internal static class SptNeutralSceneExport
{
    /// <summary>Builds a neutral scene for a SpeedTree model, or reports the behaviour that declines it.</summary>
    /// <param name="model">The parsed SpeedTree model.</param>
    /// <param name="seed">The generation seed; the same seed yields the same geometry.</param>
    /// <param name="textureResolver">Resolves diffuse texture paths to decoded pixels.</param>
    /// <param name="name">The document label, normally the source stem.</param>
    /// <param name="scene">The neutral snapshot when this returns true.</param>
    /// <param name="unsupportedReason">The declining behaviour when this returns false.</param>
    /// <param name="cancellationToken">Cancels a large graph walk.</param>
    /// <param name="options">Optional geometry options passed through to the builder.</param>
    /// <returns>True when the model was snapshotted; false when the native writer retains it.</returns>
    internal static bool TryBuild(
        SptModel model,
        uint seed,
        NifTextureResolver textureResolver,
        string name,
        out ModelDocument? scene,
        out string? unsupportedReason,
        CancellationToken cancellationToken,
        SptGeometryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        scene = null;
        var renderable = SptGeometryBuilder.Build(model, seed, options);
        var glb = NifExportSceneBuilder.BuildRenderableModel(renderable, name);
        if (glb is null)
        {
            unsupportedReason = "The SpeedTree builder produced no renderable scene.";
            return false;
        }

        return NifNeutralSceneAdapter.TryAdapt(glb, textureResolver, name, out scene, out unsupportedReason,
            cancellationToken);
    }
}
