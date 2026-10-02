namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>How <see cref="NifModelTransform" /> represents one NiAVObject local transform in the document.</summary>
internal enum NifModelTransformKind
{
    /// <summary>
    ///     The stored rotation is orthonormal within <see cref="NifModelTransform.OrthonormalTolerance" /> with a positive
    ///     determinant: explicit components with the quaternion of R^T and scale (s, s, s).
    /// </summary>
    Trs,

    /// <summary>
    ///     The stored rotation is orthonormal within tolerance with a negative determinant (a reflection): explicit
    ///     components with the quaternion of (-R)^T and scale (-s, -s, -s), which composes to exactly s R^T.
    /// </summary>
    TrsNegatedScale,

    /// <summary>The stored rotation is not orthonormal within tolerance: a matrix-only node holding S R^T T.</summary>
    Matrix
}
