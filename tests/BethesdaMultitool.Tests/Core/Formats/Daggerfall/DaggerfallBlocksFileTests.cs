using System.Text;
using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>The BLOCKS.BSA wrapper: type by name, indexed access, and the diagnostic renders.</summary>
public class DaggerfallBlocksFileTests
{
    // The enum is internal, so the theory takes its numeric value (Unknown 0, Rmb 1, Rdb 2, Rdi 3).
    [Theory]
    [InlineData("WALLAA03.RMB", 1)]
    [InlineData("n0000071.rdb", 2)]
    [InlineData("B0000000.RDI", 3)]
    [InlineData("FOO", 0)]
    [InlineData("MAPS.BSA", 0)]
    public void TypeOf_UsesTheExtension(string name, int expected)
    {
        Assert.Equal((DaggerfallBlockType)expected, DaggerfallBlocksFile.TypeOf(name));
    }

    [Fact]
    public void Open_IndexesEveryEntry_AndParsesByKind()
    {
        var rmb = DaggerfallBlockFixture.Rmb("TVRNAS00", [
            new DaggerfallBlockFixture.SubRecord(0, 0, 0, 0x0F, 5,
                new DaggerfallBlockFixture.BlockData([new DaggerfallBlockFixture.Model(310, 6, 3, 0, 0, 0, 0)], []),
                new DaggerfallBlockFixture.BlockData([], []))
        ]);
        var rdb = DaggerfallBlockFixture.Rdb(1, 1, [("72100", "DOR")],
            new Dictionary<int, IReadOnlyList<DaggerfallBlockFixture.RdbObject>>
                { [0] = [new DaggerfallBlockFixture.RdbObject(1, 0, 0, 0)] });
        var rdi = new byte[512];
        var foo = Encoding.ASCII.GetBytes("\r\n Volume in drive C has no label");
        var archive = DaggerfallBlockFixture.Archive(("TVRNAS00.RMB", rmb), ("N0000071.RDB", rdb),
            ("N0000071.RDI", rdi), ("FOO", foo));

        var directory = Path.Combine(Path.GetTempPath(), "bmt-blocks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "BLOCKS.BSA");
            File.WriteAllBytes(path, archive);

            var blocks = DaggerfallBlocksFile.Open(path);

            Assert.Equal(4, blocks.Count);
            Assert.Equal(1, blocks.IndexOf("n0000071.rdb"));
            Assert.Equal(-1, blocks.IndexOf("MISSING.RMB"));
            Assert.Equal(DaggerfallBlockType.Unknown, blocks.TypeAt(3));
            Assert.Equal(foo.Length, blocks.RecordBytes(3).Length);

            Assert.Equal("TVRNAS00", blocks.ParseRmb(0).HeaderName);
            Assert.Single(blocks.ParseRdb(1).AllObjects);
            Assert.Equal(512, blocks.RdiBytes(2).Length);
            Assert.Throws<InvalidOperationException>(() => blocks.ParseRmb(1));
            Assert.Throws<InvalidOperationException>(() => blocks.RdiBytes(0));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Renderer_ProducesImagesOfTheExpectedSize()
    {
        var rmb = DaggerfallRmbBlock.Parse(DaggerfallBlockFixture.Rmb("X", []), "X.RMB");
        var (automap, automapWidth, automapHeight) = DaggerfallBlockRenderer.RenderAutoMap(rmb, 2);
        Assert.Equal((128, 128, 128 * 128 * 4), (automapWidth, automapHeight, automap.Length));

        var (ground, groundWidth, _) = DaggerfallBlockRenderer.RenderGround(rmb, 4);
        Assert.Equal(64, groundWidth);
        Assert.Equal(64 * 64 * 4, ground.Length);
        Assert.Equal(255, ground[3]);

        var rdb = DaggerfallRdbBlock.Parse(
            DaggerfallBlockFixture.Rdb(2, 1, [],
                new Dictionary<int, IReadOnlyList<DaggerfallBlockFixture.RdbObject>>()), "N.RDB");
        var (plan, planWidth, planHeight) = DaggerfallBlockRenderer.RenderDungeonPlan(rdb, 64);
        Assert.Equal((64, 64, 64 * 64 * 4), (planWidth, planHeight, plan.Length));

        Assert.NotEqual(DaggerfallBlockRenderer.DiagnosticColor(1), DaggerfallBlockRenderer.DiagnosticColor(2));
        Assert.Equal(DaggerfallBlockRenderer.DiagnosticColor(7), DaggerfallBlockRenderer.DiagnosticColor(7));
    }
}