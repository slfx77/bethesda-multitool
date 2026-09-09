using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>What an assembly produced, and what it could not resolve.</summary>
internal sealed record DaggerfallBlockAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int Placed,
    int Resolved,
    IReadOnlyList<uint> MissingIds);

/// <summary>
///     Assembles a Daggerfall RMB block into placed meshes, so a block can be viewed or exported as
///     one scene rather than as a diagnostic plan.
///     <para>
///         The placement rules: a model's ARCH3D id is <c>ObjectId1 * 100 + ObjectId2</c>; it is
///         rotated about Y in <see cref="AngleUnitsPerTurn" /> units per full turn; and it is
///         offset by its sub-block's own position and rotation.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             The angle unit is 2,048 per TURN, so a stored unit is 360/2048 of a degree — it is
///             DIVIDED by 5.68889, never multiplied by it.
///         </b>
///         This file multiplied until 2026-09-06,
///         which skewed every rotated sub-block by a factor of 32.36 (a quarter turn rendered as
///         32.7 degrees, a half turn as 65.4). Three independent retail populations settle it:
///     </para>
///     <list type="bullet">
///         <item>
///             The 9,005 RMB sub-block rotations take EXACTLY FOUR distinct values — 0, 512, 1,024
///             and 1,536 — and nothing else. Those are the four cardinal orientations a city block
///             can take, which fixes 512 as a right angle.
///         </item>
///         <item>
///             92.07% of the 236,250 RMB per-model rotations lie in <c>[0, 2048)</c> — one turn's
///             worth — with the same four cardinals taking 66.6% of all placements and the next
///             tier (513, 1, 1,025, 1,537) sitting a fine nudge off a cardinal.
///         </item>
///         <item>
///             96.0% of the 22,961 RDB model rotations are multiples of 512, while X and Z are zero
///             on 97.9% because dungeon models stand upright.
///         </item>
///     </list>
///     <para>
///         ⚑ The oracle that settles it rather than merely fitting it is each block's own authored
///         64x64 AUTOMAP, which is independent of the placement fields. Scored on the population
///         rotation actually MOVES (a rotated sub-block, a model at least 512 units off its origin),
///         2,048-per-turn puts 32 of 32 models on a built automap cell; the old 63.28-per-turn
///         reading manages 5 of 30, which is WORSE than applying no rotation at all (17 of 32).
///         ⚠ Measured on the whole corpus the gap collapses to 0.86 against 0.80 — most placements
///         sit too near their sub-block origin for any rotation to move them, so the affected
///         population is the only one that can discriminate.
///     </para>
///     <para>
///         ⚠ The SIGN is NOT settled by that oracle and is not claimed here: both signs score 32 of
///         32 on the affected subset (17 of those rotate a half turn, where sign cannot matter, and
///         the other 15 land on built cells either way). It follows the sibling engine's measured
///         convention instead — <see cref="Battlespire.Bs6SceneAssembler" /> established raw,
///         unnegated angles on the same 2,048-unit scale, with the handedness flip done separately
///         as a frame conversion, which is exactly the shape of this transform.
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
    ///     Y rotation units in a full turn, so a quarter turn is exactly 512 units. The same scale
    ///     the sibling engine uses (<see cref="Battlespire.Bs6SceneAssembler.AngleUnitsPerTurn" />)
    ///     and the reciprocal of <see cref="DaggerfallRmbBlock.RotationDivisor" />'s 2048/360.
    /// </summary>
    public const float AngleUnitsPerTurn = 2048f;

    /// <summary>Stored units in a quarter turn, the only rotation retail city blocks use.</summary>
    public const int QuarterTurnUnits = 512;

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

        const float scale = float.Tau / AngleUnitsPerTurn;

        var model2Sub =
            Matrix4x4.CreateRotationY(model.YRotation * scale) *
            Matrix4x4.CreateTranslation(model.XPos, model.YPos, model.ZPos);

        var sub2Block =
            Matrix4x4.CreateRotationY(sub.YRotation * scale) *
            Matrix4x4.CreateTranslation(sub.XPos, 0, sub.ZPos);

        // ⚠ The mirror is a FRAME CONVERSION and is applied exactly ONCE, at the end. Mirroring
        // each position as it is composed re-mirrors: a model at z=1000 inside a sub-block at z=0
        // would land at 3096 + 4096 instead of 3096, pushing half the block outside its own square.
        return model2Sub * sub2Block * MirrorMatrix;
    }

    /// <summary>Mirrors a single Z coordinate into the block's frame.</summary>
    public static float MirrorZ(int z)
    {
        return BlockSideUnits - z;
    }
}
