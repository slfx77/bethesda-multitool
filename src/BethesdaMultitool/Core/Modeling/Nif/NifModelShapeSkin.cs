using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What <see cref="NifModelSkinReader" /> contributes to the primitives of one skinned geometry block: the vertex
///     influences, the dismember faces and their Face-domain streams, and the native facts for the primitive rows.
///     Every material variant of the geometry shares these; the node skin indices are recorded by the reader per
///     occurrence.
/// </summary>
internal sealed class NifModelShapeSkin
{
    /// <summary>Creates the contribution.</summary>
    /// <param name="influences">The typed influences, or null when the geometry is not typed as skinned.</param>
    /// <param name="faces">The dismember faces, or null.</param>
    /// <param name="faceAttributes">The Face-domain streams over <paramref name="faces" />, empty without faces.</param>
    /// <param name="facts">The primitive-row facts, or null for geometry without a skin instance.</param>
    public NifModelShapeSkin(SceneSkinInfluences? influences, SceneFaceList? faces,
        IReadOnlyList<SceneAttributeStream> faceAttributes, JsonObject? facts)
    {
        Influences = influences;
        Faces = faces;
        FaceAttributes = faceAttributes;
        Facts = facts;
    }

    /// <summary>Geometry without a skin instance: nothing to add.</summary>
    public static NifModelShapeSkin None { get; } = new(null, null, Array.Empty<SceneAttributeStream>(), null);

    /// <summary>The typed influences, or null when the geometry is not typed as skinned.</summary>
    public SceneSkinInfluences? Influences { get; }

    /// <summary>One three-corner face per primitive triangle when the dismember body parts are typed, else null.</summary>
    public SceneFaceList? Faces { get; }

    /// <summary>The Face-domain body-part and part-flag streams, appended after the geometry's own attributes.</summary>
    public IReadOnlyList<SceneAttributeStream> FaceAttributes { get; }

    /// <summary>
    ///     The facts for the <c>bmt.nif.primitive</c> rows, or null without a skin instance. Built once per geometry; each
    ///     row deep-clones it.
    /// </summary>
    public JsonObject? Facts { get; }
}
