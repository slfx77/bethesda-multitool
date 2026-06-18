using System.Text;
using FalloutXbox360Utils.Core.Formats.SpeedTree;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.SpeedTree;

public class SptFileTests
{
    private static readonly string?[] ShrubCandidates =
    [
        @"Sample\Meshes\meshes_360_final\trees\wastelandshrub01.spt",
        @"Sample\Meshes\meshes_360_proto\trees\wastelandshrub01.spt",
    ];

    // ---- Pure-unit: BezierSpline text parsing ----

    [Fact]
    public void BezierSpline_ParsesHeaderAndControlPoints()
    {
        const string text =
            "BezierSpline 0 1 50 { 2  0 0 0.707107 0.707107 0.079604   1 0.519875 0.999636 -0.0269686 0.871068 }";

        var spline = SptBezierSpline.Parse(text);

        Assert.NotNull(spline);
        Assert.Equal(0f, spline!.Header.X);
        Assert.Equal(1f, spline.Header.Y);
        Assert.Equal(50f, spline.Header.Z);
        Assert.Equal(2, spline.ControlPoints.Count);
        Assert.Equal(0f, spline.ControlPoints[0].Param);
        Assert.Equal(0.707107f, spline.ControlPoints[0].B, 5);
        Assert.Equal(1f, spline.ControlPoints[1].Param);
        Assert.Equal(0.871068f, spline.ControlPoints[1].D, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NotASpline 1 2 3")]
    [InlineData("BezierSpline 0 1")] // too short / no brace
    public void BezierSpline_RejectsMalformed(string text)
    {
        Assert.Null(SptBezierSpline.Parse(text));
    }

    // ---- Pure-unit: cursor primitives ----

    [Fact]
    public void Cursor_ReadsTokensFloatsAndStrings()
    {
        var buf = new List<byte>();
        buf.AddRange(BitConverter.GetBytes(1000u));        // token
        buf.AddRange(BitConverter.GetBytes(12u));          // string length
        buf.AddRange(Encoding.ASCII.GetBytes("__IdvSpt_02_"));
        buf.AddRange(BitConverter.GetBytes(3.5f));         // float
        buf.Add(0x01);                                     // bool byte

        var c = new SptCursor(buf.ToArray());
        Assert.Equal(1000u, c.ReadToken());
        Assert.Equal("__IdvSpt_02_", c.ReadString());
        Assert.Equal(3.5f, c.ReadFloat());
        Assert.True(c.ReadBool());
    }

    // ---- Real-file: wastelandshrub01.spt ----

    [Fact]
    public void Parse_WastelandShrub01_HasBranchesLeavesAndBarkTexture()
    {
        var path = ResolveShrub();
        Assert.SkipWhen(path is null, "Missing sample: wastelandshrub01.spt");

        var model = SptFile.Parse(File.ReadAllBytes(path!));

        // Bark texture (a dev-machine absolute .tga path in shipped files).
        Assert.NotNull(model.General.BarkTexturePath);
        Assert.Contains("WastelandShrub01Bark", model.General.BarkTexturePath!, StringComparison.OrdinalIgnoreCase);

        // 4 branches, each with all nine spline slots populated.
        Assert.Equal(4, model.Branches.Count);
        foreach (var branch in model.Branches)
        {
            Assert.Equal(9, branch.Splines.Count);
            Assert.All(branch.Splines, s =>
            {
                Assert.NotNull(s);
                Assert.NotEmpty(s!.ControlPoints);
            });
        }

        // 4 leaf cards, each with a position and a material/texture path.
        Assert.Equal(4, model.Leaves.Count);
        Assert.All(model.Leaves, leaf => Assert.NotNull(leaf.Material));
    }

    [Fact]
    public void TryParse_NonSptBytes_ReturnsNull()
    {
        Assert.Null(SptFile.TryParse(Encoding.ASCII.GetBytes("this is not a speedtree file at all")));
    }

    private static string? ResolveShrub()
    {
        foreach (var rel in ShrubCandidates)
        {
            var p = SampleFileFixture.FindSamplePath(rel!);
            if (p is not null)
            {
                return p;
            }
        }

        return null;
    }
}
