namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     One NiSkinData BoneData entry for <see cref="NifTestBlockLayouts.SkinData" /> (nif.xml:6986-7009): the bone's Skin
///     Transform (rotation row by row, translation, uniform scale), its bounding sphere and its (vertex, weight) pairs in
///     the order they are written. Nothing here consults NifSchema.
/// </summary>
internal sealed class NifTestSkinBone
{
    /// <summary>The Matrix33 rotation, nine floats row by row (file order).</summary>
    public float[] Rotation { get; init; } = NifTestBlockLayouts.Identity;

    /// <summary>The translation.</summary>
    public (float X, float Y, float Z) Translation { get; init; }

    /// <summary>The uniform scale.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>The bounding sphere: center xyz then radius.</summary>
    public float[] BoundingSphere { get; init; } = [0f, 0f, 0f, 1f];

    /// <summary>The (vertex index, weight) pairs, written only when the skin data has vertex weights.</summary>
    public (ushort Vertex, float Weight)[] Weights { get; init; } = [];
}
