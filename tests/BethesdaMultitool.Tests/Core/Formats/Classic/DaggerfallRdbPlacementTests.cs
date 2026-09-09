using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Pins what is EXACT about RDB (dungeon block) object placement, measured before any
///     assembler is written for them — the RMB side settled its conventions empirically and this
///     does the same rather than assuming RMB's 4,096-unit square carries over.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallRdbPlacementTests
{
    [Fact]
    public void EveryRdbModelIndexResolvesToANumberedMeshId()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var arena2 = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(arena2 is null, RealAssetPaths.SkipMessage("Daggerfall"));

        var path = Path.Combine(arena2, "BLOCKS.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BLOCKS.BSA"));

        var blocks = DaggerfallBlocksFile.Open(path);
        var lines = new List<string>();

        var rdbCount = 0;
        var objects = 0;
        var withModel = 0;
        var resolved = 0;
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        int minZ = int.MaxValue, maxZ = int.MinValue;
        var rotations = new HashSet<int>();

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks.TypeAt(i) != DaggerfallBlockType.Rdb)
            {
                continue;
            }

            DaggerfallRdbBlock block;
            try
            {
                block = blocks.ParseRdb(i);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            rdbCount++;
            foreach (var o in block.AllObjects)
            {
                objects++;
                minX = Math.Min(minX, o.XPos);
                maxX = Math.Max(maxX, o.XPos);
                minY = Math.Min(minY, o.YPos);
                maxY = Math.Max(maxY, o.YPos);
                minZ = Math.Min(minZ, o.ZPos);
                maxZ = Math.Max(maxZ, o.ZPos);

                if (o.Model is not { } model)
                {
                    continue;
                }

                withModel++;
                rotations.Add(model.YRotation);
                if (model.ModelIndex < block.ModelReferences.Count
                    && block.ModelReferences[model.ModelIndex].ModelIdNumber is not null)
                {
                    resolved++;
                }
            }
        }

        lines.Add($"RDB blocks parsed : {rdbCount}");
        lines.Add($"objects           : {objects}");
        lines.Add($"with a model      : {withModel}");
        lines.Add($"model index resolves to a numbered id: {resolved} of {withModel}");
        lines.Add($"X range           : {minX} .. {maxX}");
        lines.Add($"Y range           : {minY} .. {maxY}");
        lines.Add($"Z range           : {minZ} .. {maxZ}");
        lines.Add($"distinct Y rotations: {rotations.Count}, min {rotations.Min()}, max {rotations.Max()}");

        Directory.CreateDirectory("TestOutput");
        File.WriteAllLines(Path.Combine("TestOutput", "daggerfall-rdb-extents.txt"), lines);

        Assert.True(rdbCount > 0, "no RDB blocks parsed");

        // ⚑ THE EXACT ONE: every object that carries a model resolves its ModelIndex to a numbered
        // mesh id through the block's own reference table — 22,961 of 22,961 on the 187 retail RDB
        // blocks. That is what makes an RDB assembler possible: no placement is orphaned.
        Assert.Equal(withModel, resolved);
        Assert.True(withModel > 20_000, $"expected ~22,961 modelled objects, saw {withModel}");

        // ⚠⚠ RDB objects do NOT fit a 2,048-unit block, which CLAUDE.md previously stated: Z reaches
        // 2,712 and X 2,064 on retail data. And Y goes NEGATIVE (dungeons descend), so an RDB
        // assembler cannot reuse RMB's non-negative 4,096-unit square assumption.
        Assert.True(maxZ > 2_048, $"Z reaches {maxZ}, so the 2,048-unit block claim is false");
        Assert.True(minY < 0, $"Y minimum is {minY}; dungeons descend below the origin");
    }
}