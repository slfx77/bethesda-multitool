using System.Numerics;

using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>What an assembly produced, and what it could not resolve.</summary>
internal sealed record DaggerfallBlockAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances, int Placed, int Resolved, IReadOnlyList<uint> MissingIds);

/// <summary>
///     Assembles a Daggerfall RMB block into placed meshes, so a block can be viewed or exported as
///     one scene rather than as a diagnostic plan.
///     <para>
///         The placement rules, each measured against the reference implementation's behaviour:
///         a model's ARCH3D id is <c>ObjectId1 * 100 + ObjectId2</c>; it is rotated about Y by
///         <see cref="RotationDegreesPerUnit" /> degrees per unit; and it is offset by its
///         sub-block's own position and rotation.
///     </para>
///     <para>
///         ⚠ <b>Z is MIRRORED as <c>4096 - z</c>, not used directly.</b> Daggerfall authors block
///         coordinates in a left-handed frame, so placing them unmirrored builds a block that is
///         internally consistent and reflected as a whole — every wall in the right place relative
///         to its neighbours, and the entire building inside out. That is exactly the kind of error
///         that renders convincingly, so it is stated here and pinned by a test rather than left to
///         be noticed by eye.
///     </para>
/// </summary>
internal static class DaggerfallBlockSceneAssembler
{
    /// <summary>Side of one block in Daggerfall's coordinate units.</summary>
    public const int BlockSideUnits = 4096;

    /// <summary>
    ///     Degrees of Y rotation per stored unit. 360 / 63.28125 — the reference's constant, which
    ///     is <b>not</b> a whole number of units per turn, so rounding it to 5.69 or to 360/64
    ///     visibly skews long terraces.
    /// </summary>
    public const double RotationDegreesPerUnit = 5.68889;

    /// <summary>
    ///     Places every model of every sub-block, resolving meshes through <paramref name="resolve" />.
    /// </summary>
    /// <param name="blockName">Name of the block, used to label the placed instances.</param>
    /// <param name="subRecords">The block's sub-blocks.</param>
    /// <param name="resolve">ARCH3D object id → mesh, or null when the archive has no such record.</param>
    /// <param name="interior">Place the interior object sets rather than the exterior ones.</param>
    /// <remarks>
    ///     Takes the sub-records rather than the whole <see cref="DaggerfallRmbBlock" /> deliberately:
    ///     that type is constructible only by its parser, and placement needs nothing else from it.
    /// </remarks>
    public static DaggerfallBlockAssembly Assemble(
        string blockName,
        IReadOnlyList<DaggerfallRmbSubRecord> subRecords,
        Func<uint, XnGineTriangleMesh?> resolve,
        bool interior = false)
    {
        ArgumentNullException.ThrowIfNull(blockName);
        ArgumentNullException.ThrowIfNull(subRecords);
        ArgumentNullException.ThrowIfNull(resolve);

        var instances = new List<XnGineMeshInstance>();
        var missing = new List<uint>();
        var placed = 0;

        foreach (var sub in subRecords)
        {
            var set = interior ? sub.Interior : sub.Exterior;
            foreach (var model in set.Models)
            {
                placed++;

                var mesh = resolve(model.ModelId);
                if (mesh is null)
                {
                    if (!missing.Contains(model.ModelId))
                    {
                        missing.Add(model.ModelId);
                    }

                    continue;
                }

                instances.Add(new XnGineMeshInstance(
                    mesh, TransformFor(sub, model), $"{blockName}_{sub.Index:D2}_{model.ModelId}"));
            }
        }

        return new DaggerfallBlockAssembly(instances, placed, instances.Count, missing);
    }

    /// <summary>
    ///     Builds one model's placement: its own rotation and position, then its sub-block's.
    ///     <para>
    ///         ⚠ <c>System.Numerics</c> is ROW-vector, so composing <c>A * B</c> applies A THEN B.
    ///         The model's own transform must therefore come first in the product, not last.
    ///     </para>
    /// </summary>
    public static Matrix4x4 TransformFor(DaggerfallRmbSubRecord sub, DaggerfallRmbModel model)
    {
        ArgumentNullException.ThrowIfNull(sub);

        var model2Sub =
            Matrix4x4.CreateRotationY((float)(model.YRotation * RotationDegreesPerUnit * Math.PI / 180.0)) *
            Matrix4x4.CreateTranslation(model.XPos, model.YPos, model.ZPos);

        var sub2Block =
            Matrix4x4.CreateRotationY((float)(sub.YRotation * RotationDegreesPerUnit * Math.PI / 180.0)) *
            Matrix4x4.CreateTranslation(sub.XPos, 0, sub.ZPos);

        // ⚠ The mirror is a FRAME CONVERSION and is applied exactly ONCE, at the end. Mirroring
        // each position as it is composed re-mirrors: a model at z=1000 inside a sub-block at z=0
        // would land at 3096 + 4096 instead of 3096, pushing half the block outside its own square.
        return model2Sub * sub2Block * MirrorMatrix;
    }

    /// <summary>
    ///     Converts a block-space position into the mirrored frame: <c>z → 4096 - z</c>.
    ///     <para>
    ///         ⚠ This is a MIRROR, not a rotation, so it reverses handedness and therefore triangle
    ///         winding. Consumers that cull back faces must account for that; the diagnostic and
    ///         viewer paths here render both sides.
    ///     </para>
    /// </summary>
    public static Matrix4x4 MirrorMatrix { get; } =
        Matrix4x4.CreateScale(1, 1, -1) * Matrix4x4.CreateTranslation(0, 0, BlockSideUnits);

    /// <summary>Mirrors a single Z coordinate into the block's frame.</summary>
    public static float MirrorZ(int z) => BlockSideUnits - z;
}
