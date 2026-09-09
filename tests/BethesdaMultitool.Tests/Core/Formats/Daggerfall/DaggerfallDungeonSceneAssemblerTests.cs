using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Pins whole-dungeon layout — above all the Z NEGATION, which is the rule whose violation
///     still produces a dungeon that looks entirely plausible block by block.
/// </summary>
public sealed class DaggerfallDungeonSceneAssemblerTests
{
    private static DaggerfallDungeonBlock Block(int x, int z)
    {
        return new DaggerfallDungeonBlock((sbyte)x, (sbyte)z, 0, 0, false);
    }

    [Fact]
    public void GridOffset_NegatesTheZIndexButNotTheXIndex()
    {
        // ⚠⚠ THE rule. A block's own transform already mirrors z to 2048 - z, so a block offset
        // composed after it must be negated to stay in that frame. Adding +Z*2048 reflects the whole
        // grid about the Z axis: every block internally correct, the dungeon laid out backwards.
        var offset = DaggerfallDungeonSceneAssembler.GridOffset(Block(3, 2));

        Assert.Equal(3 * 2048f, offset.Translation.X, 3);
        Assert.Equal(0f, offset.Translation.Y, 3);
        Assert.Equal(-2 * 2048f, offset.Translation.Z, 3);
    }

    [Theory]
    [InlineData(0, 0, 0f, 0f)]
    [InlineData(1, 0, 2048f, 0f)]
    [InlineData(0, 1, 0f, -2048f)]
    [InlineData(-2, -3, -4096f, 6144f)]
    public void GridOffset_PlacesBlocksOnTheSignedGrid(int x, int z, float expectedX, float expectedZ)
    {
        // ⚠ X and Z are SIGNED — a dungeon grows in all four directions from its start block, so a
        // byte read would fold the negative half onto the positive one.
        var offset = DaggerfallDungeonSceneAssembler.GridOffset(Block(x, z));

        Assert.Equal(expectedX, offset.Translation.X, 3);
        Assert.Equal(expectedZ, offset.Translation.Z, 3);
    }

    [Fact]
    public void GridOffset_ComposesWithTheBlockMirrorToMirrorTheWholeDungeonOnce()
    {
        // The composition this exists to get right: a point at local z inside the block at Z index 1
        // must land at 2048 - (1*2048 + z), i.e. the whole dungeon mirrored about one constant —
        // NOT at (2048 - z) + 2048, which is what adding the offset unnegated would give.
        const float localZ = 500f;
        const int blockZ = 1;

        var afterBlockMirror = DaggerfallRdbSceneAssembler.MirrorMatrix.Translation.Z - localZ;
        var final = afterBlockMirror + DaggerfallDungeonSceneAssembler.GridOffset(Block(0, blockZ)).Translation.Z;

        Assert.Equal(2048f - (blockZ * 2048f + localZ), final, 3);
    }

    [Fact]
    public void Assemble_ReportsBlocksItCannotResolveWithoutFailing()
    {
        var result = DaggerfallDungeonSceneAssembler.Assemble(
            "PRIVATEER",
            [Block(0, 0), Block(1, 0)],
            _ => null,
            _ => null);

        Assert.Empty(result.Instances);
        Assert.Equal(0, result.BlocksPlaced);
        // Both references name the SAME block (index 0, number 0), so the missing list de-duplicates.
        Assert.Equal<string[]>(["N0000000.RDB"], [.. result.MissingBlockNames]);
    }

    [Fact]
    public void Assemble_NamesEachBlockThroughTheMeasuredLetterTable()
    {
        var asked = new List<string>();
        DaggerfallDungeonSceneAssembler.Assemble(
            "D",
            [new DaggerfallDungeonBlock(0, 0, 2, 0, true), new DaggerfallDungeonBlock(1, 0, 999, 3, false)],
            name =>
            {
                asked.Add(name);
                return null;
            },
            _ => null);

        Assert.Equal<string[]>(["N0000002.RDB", "S0000999.RDB"], [.. asked]);
    }

    [Fact]
    public void BlockGridUnits_MatchesTheDungeonBlockSide()
    {
        // ⚠ 2,048 — HALF the 4,096-unit RMB city block. Using the city side spreads a dungeon to
        // twice its footprint with every block still internally correct.
        Assert.Equal(2048, DaggerfallDungeonSceneAssembler.BlockGridUnits);
        Assert.Equal(DaggerfallRdbBlock.UnitsPerBlock, DaggerfallDungeonSceneAssembler.BlockGridUnits);
        Assert.NotEqual(DaggerfallBlockSceneAssembler.BlockSideUnits, DaggerfallDungeonSceneAssembler.BlockGridUnits);
    }
}