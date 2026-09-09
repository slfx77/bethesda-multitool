using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>What a dungeon-block assembly produced, and what it could not resolve.</summary>
internal sealed record DaggerfallRdbAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int Placed,
    int Resolved,
    IReadOnlyList<uint> MissingIds);

/// <summary>
///     Assembles a Daggerfall dungeon block (<c>*.RDB</c>) into placed meshes — the other half of
///     <see cref="DaggerfallBlockSceneAssembler" />, which does the same for the exterior
///     <c>*.RMB</c> city blocks.
///     <para>
///         ⚑ <b>Every placement resolves.</b> Across all 187 retail dungeon blocks there are 39,467
///         objects, 22,961 of them modelled, and all 22,961 resolve their
///         <see cref="DaggerfallRdbModelResource.ModelIndex" /> through the block's own 750-slot
///         reference table to a numbered ARCH3D id. Nothing is orphaned, so a dungeon block can be
///         assembled whole rather than in part. The index is used DIRECTLY — it is a slot number,
///         not a byte offset needing division.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             An RDB does not inherit the RMB block's conventions, and three of them do not
///             transfer.
///         </b>
///         Measured over those 22,961 placements (2026-09-06):
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Y goes NEGATIVE</b> — the range is −4,608..3,009. Dungeons descend below their
///             entrance, so the RMB assumption that a block occupies a non-negative square is false
///             here and a Y-clamping viewer would hide over half of a dungeon.
///         </item>
///         <item>
///             <b>X and Z reach past one block</b> — X spans 0..2,064 and Z 0..2,712 against
///             <see cref="DaggerfallRdbBlock.UnitsPerBlock" />'s 2,048. Seven objects are the strays;
///             the block side is a rule with exceptions, never a bound to validate against.
///         </item>
///         <item>
///             <b>All three axes rotate</b>, unlike an RMB model which only ever yaws — though X and
///             Z are zero on 97.9% of placements because dungeon geometry stands upright.
///         </item>
///     </list>
///     <para>
///         ⚑ The angle scale IS shared: 2,048 units per turn, 96.0% of Y rotations being multiples
///         of the 512-unit right angle. That is the same constant the RMB assembler and
///         <see cref="Battlespire.Bs6SceneAssembler" /> use, and the composition order follows the
///         sibling engine's measured one — Y, then X, then Z, unnegated, translation last.
///     </para>
///     <para>
///         ⚠ The vertical SIGN and the Z mirror are taken from the RMB path rather than measured
///         here, so that a dungeon and a city block share one space; they are a frame convention,
///         not a claim about the data. <see cref="DaggerfallBlockSceneAssembler.MirrorMatrix" />
///         cannot be reused directly because it folds in the 4,096-unit RMB block side — a dungeon
///         block is half that.
///     </para>
/// </summary>
internal static class DaggerfallRdbSceneAssembler
{
    /// <summary>Y rotation units in a full turn, shared with the RMB and Battlespire assemblers.</summary>
    public const float AngleUnitsPerTurn = DaggerfallBlockSceneAssembler.AngleUnitsPerTurn;

    /// <summary>
    ///     Converts a dungeon-block position into the mirrored frame: <c>z → 2048 - z</c>.
    ///     <para>
    ///         ⚠ A MIRROR, not a rotation, so it reverses handedness and therefore triangle winding —
    ///         the same caveat the RMB path carries. It is applied exactly ONCE, at the end of the
    ///         product; mirroring a position before composing re-mirrors it.
    ///     </para>
    /// </summary>
    public static Matrix4x4 MirrorMatrix { get; } =
        Matrix4x4.CreateScale(1, 1, -1) * Matrix4x4.CreateTranslation(0, 0, DaggerfallRdbBlock.UnitsPerBlock);

    /// <summary>
    ///     Places every modelled object in the block, resolving meshes through
    ///     <paramref name="resolve" />. Lights and flats are not geometry and are skipped; they are
    ///     still on the block for a caller that wants them.
    /// </summary>
    /// <param name="blockName">Name of the block, used to label the placed instances.</param>
    /// <param name="references">The block's 750 model reference slots.</param>
    /// <param name="objects">The block's placed objects, across every list.</param>
    /// <param name="resolve">ARCH3D object id → mesh, or null when the archive has no such record.</param>
    /// <remarks>
    ///     Takes the references and objects rather than the whole <see cref="DaggerfallRdbBlock" />
    ///     for the same reason the RMB assembler takes sub-records: that type is constructible only
    ///     by its parser, and placement needs nothing else from it.
    /// </remarks>
    public static DaggerfallRdbAssembly Assemble(
        string blockName,
        IReadOnlyList<DaggerfallRdbModelReference> references,
        IEnumerable<DaggerfallRdbObject> objects,
        Func<uint, XnGineTriangleMesh?> resolve)
    {
        ArgumentNullException.ThrowIfNull(blockName);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(resolve);

        var instances = new List<XnGineMeshInstance>();
        var missing = new List<uint>();
        var placed = 0;

        foreach (var placement in objects)
        {
            if (placement.Model is not { } model)
            {
                continue;
            }

            placed++;

            var id = ModelIdOf(references, model);
            if (id is null)
            {
                continue;
            }

            var mesh = resolve(id.Value);
            if (mesh is null)
            {
                if (!missing.Contains(id.Value))
                {
                    missing.Add(id.Value);
                }

                continue;
            }

            instances.Add(new XnGineMeshInstance(
                mesh, TransformFor(placement, model), $"{blockName}_{placement.Position}_{id.Value}"));
        }

        return new DaggerfallRdbAssembly(instances, placed, instances.Count, missing);
    }

    /// <summary>
    ///     The ARCH3D id a model placement names, or null when its slot holds no numbered id.
    ///     <para>
    ///         ⚠ The reference table has 750 slots and a block fills only the first few; the unused
    ///         tail holds leftover bytes whose 5 characters need not parse as a number. Returning
    ///         null keeps that a skipped placement rather than an exception — though on retail every
    ///         one of the 22,961 modelled objects points at a slot that does parse.
    ///     </para>
    /// </summary>
    public static uint? ModelIdOf(
        IReadOnlyList<DaggerfallRdbModelReference> references, DaggerfallRdbModelResource model)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(model);

        return model.ModelIndex < references.Count ? references[model.ModelIndex].ModelIdNumber : null;
    }

    /// <summary>
    ///     One object's placement: its three rotations applied Y, then X, then Z, then its position,
    ///     then the frame conversion.
    ///     <para>
    ///         ⚠ <c>System.Numerics</c> is ROW-vector, so <c>A * B</c> applies A THEN B — the
    ///         rotations must precede the translation in the product, not follow it.
    ///     </para>
    /// </summary>
    public static Matrix4x4 TransformFor(DaggerfallRdbObject placement, DaggerfallRdbModelResource model)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(model);

        const float scale = float.Tau / AngleUnitsPerTurn;

        return Matrix4x4.CreateRotationY(model.YRotation * scale)
               * Matrix4x4.CreateRotationX(model.XRotation * scale)
               * Matrix4x4.CreateRotationZ(model.ZRotation * scale)
               * Matrix4x4.CreateTranslation(placement.XPos, placement.YPos, placement.ZPos)
               * MirrorMatrix;
    }
}
