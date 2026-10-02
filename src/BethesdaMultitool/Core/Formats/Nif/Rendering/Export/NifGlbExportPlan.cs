using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>A planned NIF GLB export: the decision and, for the normalized route, the documents already built.</summary>
/// <remarks>
///     Planning runs the neutral adapter before any native write, because the native writer normalizes winding on the
///     source scene in place while the adapter works on clones. A plan is therefore made first and written second.
/// </remarks>
internal sealed class NifGlbExportPlan
{
    /// <summary>Creates a plan, requiring both documents exactly when the decision takes the normalized route.</summary>
    /// <param name="decision">The routing decision.</param>
    /// <param name="sceneDocument">The neutral snapshot, required only for the normalized route.</param>
    /// <param name="gltfDocument">The built shared glTF graph, required only for the normalized route.</param>
    /// <exception cref="ArgumentNullException">The decision is null.</exception>
    /// <exception cref="ArgumentException">The documents do not match the decision's route.</exception>
    public NifGlbExportPlan(NifGlbExportDecision decision, ModelDocument? sceneDocument, GltfDocument? gltfDocument)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var normalized = decision.IsNormalized;
        if (normalized && (sceneDocument is null || gltfDocument is null))
        {
            throw new ArgumentException("A normalized plan requires its scene and glTF documents.", nameof(decision));
        }

        if (!normalized && (sceneDocument is not null || gltfDocument is not null))
        {
            throw new ArgumentException("A native or refused plan carries no normalized documents.", nameof(decision));
        }

        Decision = decision;
        ModelDocument = sceneDocument;
        GltfDocument = gltfDocument;
    }

    /// <summary>The routing decision.</summary>
    public NifGlbExportDecision Decision { get; }

    /// <summary>The neutral snapshot for the normalized route; null otherwise.</summary>
    public ModelDocument? ModelDocument { get; }

    /// <summary>The built shared glTF graph for the normalized route; null otherwise.</summary>
    public GltfDocument? GltfDocument { get; }
}
