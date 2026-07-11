using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     A skinned submesh's CPU skinning inputs, exposed (not baked) so the viewer can re-pose it per
///     frame: raw skin-space base geometry, packed top-4 vertex influences, per-skin-bone inverse
///     bind poses, and the mapping from skin-bone slots to the mesh's animation-rig bone indices.
///     <para>
///         Space contract (matches <c>NifSubmeshExtractor.ApplySkinningOrTransform</c>): skinned
///         vertices bypass the shape node's world transform entirely — LBS input is the RAW
///         shape-local vertex buffer, and <c>Σ w·(v × skinMatrix)</c> lands in model space, where
///         the placement world matrix applies. Skinning at the rest pose reproduces the statically
///         baked vertices.
///     </para>
/// </summary>
internal sealed record NifSubmeshSkin(
    float[] BasePositions,
    float[]? BaseNormals,
    Matrix4x4[] InverseBindPoses,
    int[] SkinBoneToAnimBone,
    byte[] BoneIndices,
    float[] BoneWeights)
{
    public int VertexCount => BasePositions.Length / 3;
}
