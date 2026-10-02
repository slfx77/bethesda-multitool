using System.Numerics;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>One drawn triangle occurrence: its corners in authored winding order and their world positions.</summary>
/// <param name="primitive">The index of the owning <see cref="NifGlbDecodedPrimitive" /> occurrence.</param>
/// <param name="corners">Exactly three corners in index order.</param>
/// <param name="world">The three corners' rest-world positions, in the same order.</param>
internal sealed class NifGlbDecodedTriangle(int primitive, NifGlbDecodedCorner[] corners, Vector3[] world)
{
    /// <summary>The index of the owning primitive occurrence in its <see cref="NifGlbDecodedModel" />.</summary>
    internal int Primitive { get; } = primitive;

    /// <summary>The three corners in authored winding order.</summary>
    internal NifGlbDecodedCorner[] Corners { get; } = corners;

    /// <summary>The three rest-world corner positions, parallel to <see cref="Corners" />.</summary>
    internal Vector3[] World { get; } = world;

    /// <summary>The mean of the three world positions, used only to choose a spatial cell.</summary>
    internal Vector3 Centroid => (World[0] + World[1] + World[2]) / 3f;

    /// <summary>
    ///     Whether two corners share an exactly equal local position. This is the native toolkit's rejection rule,
    ///     measured on SharpGLTF.Toolkit 1.0.6: equal positions with different texture coordinates are rejected,
    ///     positive and negative zero compare equal, and distinct collinear positions are kept.
    /// </summary>
    internal bool IsRepeatedPosition =>
        Corners[0].Position.Equals(Corners[1].Position) ||
        Corners[1].Position.Equals(Corners[2].Position) ||
        Corners[2].Position.Equals(Corners[0].Position);
}
