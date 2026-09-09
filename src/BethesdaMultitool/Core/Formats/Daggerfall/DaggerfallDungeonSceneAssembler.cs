using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>What a dungeon assembly produced, and what it could not resolve.</summary>
internal sealed record DaggerfallDungeonAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int BlocksPlaced,
    int BlocksMissing,
    int Placed,
    IReadOnlyList<string> MissingBlockNames);

/// <summary>
///     Assembles a WHOLE dungeon — every <c>*.RDB</c> block laid out on its grid — rather than one
///     block at a time. Blocks are named through <see cref="DaggerfallDungeonBlockName" /> and their
///     interiors built by <see cref="DaggerfallRdbSceneAssembler" />.
///     <para>
///         ⚑ Retail carries 4,232 dungeons over 40,263 block references, and every one resolves, so
///         a dungeon always assembles whole. A dungeon is a sparse grid: each block records a signed
///         <c>X</c>/<c>Z</c> index and occupies <see cref="DaggerfallRdbBlock.UnitsPerBlock" /> units
///         on each side.
///     </para>
///     <para>
///         ⚠⚠ <b>The block's Z index is NEGATED, its X index is not.</b> This is not a preference —
///         it falls out of the per-block Z mirror. A block's own transform already maps a local
///         <c>z</c> to <c>2048 − z</c>, so composing a block offset AFTER it must negate that offset
///         to stay in the same mirrored frame: the dungeon-space point <c>(Z*2048 + z)</c> mirrors to
///         <c>2048 − Z*2048 − z</c>, which is the per-block result <c>2048 − z</c> plus
///         <c>−Z*2048</c>. Adding <c>+Z*2048</c> instead reflects the grid about the Z axis, and the
///         result is a dungeon that is internally consistent block by block and laid out backwards
///         as a whole — the same failure mode the RMB mirror has, and just as convincing to look at.
///     </para>
/// </summary>
internal static class DaggerfallDungeonSceneAssembler
{
    /// <summary>Units a block occupies along each grid axis.</summary>
    public const int BlockGridUnits = DaggerfallRdbBlock.UnitsPerBlock;

    /// <summary>
    ///     Places every block of a dungeon.
    /// </summary>
    /// <param name="dungeonName">Name used to label the placed instances.</param>
    /// <param name="blocks">The dungeon's block references, from <c>MAPS.BSA</c>.</param>
    /// <param name="resolveBlock">Entry name (e.g. <c>N0000002.RDB</c>) → parsed block, or null.</param>
    /// <param name="resolveMesh">ARCH3D object id → mesh, or null when the archive has no record.</param>
    public static DaggerfallDungeonAssembly Assemble(
        string dungeonName,
        IEnumerable<DaggerfallDungeonBlock> blocks,
        Func<string, DaggerfallRdbBlock?> resolveBlock,
        Func<uint, XnGineTriangleMesh?> resolveMesh)
    {
        ArgumentNullException.ThrowIfNull(dungeonName);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(resolveBlock);
        ArgumentNullException.ThrowIfNull(resolveMesh);

        var instances = new List<XnGineMeshInstance>();
        var missing = new List<string>();
        var placedBlocks = 0;
        var placedObjects = 0;

        foreach (var reference in blocks)
        {
            var name = DaggerfallDungeonBlockName.Resolve(reference);
            var block = name is null ? null : resolveBlock(name);
            if (block is null)
            {
                if (name is not null && !missing.Contains(name))
                {
                    missing.Add(name);
                }

                continue;
            }

            var assembly = DaggerfallRdbSceneAssembler.Assemble(
                block.Name, block.ModelReferences, block.AllObjects, resolveMesh);
            placedBlocks++;
            placedObjects += assembly.Placed;

            var toGrid = GridOffset(reference);
            foreach (var instance in assembly.Instances)
            {
                instances.Add(instance with
                {
                    Transform = instance.Transform * toGrid,
                    Name = $"{dungeonName}_{reference.X}_{reference.Z}_{instance.Name}"
                });
            }
        }

        return new DaggerfallDungeonAssembly(instances, placedBlocks, missing.Count, placedObjects, missing);
    }

    /// <summary>
    ///     One block's offset on the dungeon grid.
    ///     <para>
    ///         ⚠⚠ Z is NEGATED and X is not — see the type remarks. This is applied AFTER the block's
    ///         own transform, which has already mirrored Z.
    ///     </para>
    /// </summary>
    public static Matrix4x4 GridOffset(DaggerfallDungeonBlock block)
    {
        return Matrix4x4.CreateTranslation(block.X * BlockGridUnits, 0, -block.Z * BlockGridUnits);
    }
}
