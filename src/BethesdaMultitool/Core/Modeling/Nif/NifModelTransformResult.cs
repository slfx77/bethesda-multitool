using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of <see cref="NifModelTransform.Resolve" />: the representation chosen for one NiAVObject transform
///     plus the measurements that justify it, which the reader keeps in native state.
/// </summary>
/// <param name="Kind">TRS, TRS with a negated uniform scale, or a matrix-only node.</param>
/// <param name="Trs">The explicit components for the two TRS kinds; null for <see cref="NifModelTransformKind.Matrix" />.</param>
/// <param name="Matrix">
///     The document's row-vector local matrix: <see cref="SceneTrs.ToMatrix" /> of <paramref name="Trs" /> for the TRS
///     kinds, or S R^T T composed in double and rounded once per element for a matrix node.
/// </param>
/// <param name="OrthonormalityError">E = max |R R^T - I| over the stored rotation, computed in double.</param>
/// <param name="Determinant">The determinant of the stored rotation, computed in double.</param>
/// <param name="QuaternionReconstructionError">
///     For the TRS kinds, the largest absolute difference between the rotation matrix rebuilt from the Float32
///     quaternion and the (possibly negated) transposed stored rotation, in double; null for a matrix node. TRS is exact
///     only up to this value, because the threshold admits rotations within 1e-5 of orthonormal.
/// </param>
internal sealed record NifModelTransformResult(
    NifModelTransformKind Kind,
    SceneTrs? Trs,
    Matrix4x4 Matrix,
    double OrthonormalityError,
    double Determinant,
    double? QuaternionReconstructionError);
