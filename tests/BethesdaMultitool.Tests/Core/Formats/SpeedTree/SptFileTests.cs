using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

[Trait("Category", TestCategories.BucketB)]
public class SptFileTests
{
    private static readonly string?[] ShrubCandidates =
    [
        @"Sample\Meshes\meshes_360_final\trees\wastelandshrub01.spt",
        @"Sample\Meshes\meshes_360_proto\trees\wastelandshrub01.spt"
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
        buf.AddRange(BitConverter.GetBytes(1000u)); // token
        buf.AddRange(BitConverter.GetBytes(12u)); // string length
        buf.AddRange(Encoding.ASCII.GetBytes("__IdvSpt_02_"));
        buf.AddRange(BitConverter.GetBytes(3.5f)); // float
        buf.Add(0x01); // bool byte

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
        BucketBTestGuard.SkipUnlessEnabled();
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

        // Post-EndFile token 10000/10002 supplies the exact CLeafGeometry::SetTextureCoords blocks.
        Assert.Equal(4, model.LeafTextureCoords.Count);
        Assert.Equal(0.5f, model.LeafTextureCoords[0][0].X, 5);
        Assert.Equal(1f, model.LeafTextureCoords[0][0].Y, 5);
        Assert.Equal(0f, model.LeafTextureCoords[0][1].X, 5);
        Assert.Equal(1f, model.LeafTextureCoords[0][1].Y, 5);
        Assert.Equal(1f, model.LeafTextureCoords[2][0].X, 5);
        Assert.Equal(0.5f, model.LeafTextureCoords[2][1].X, 5);
    }

    [Theory]
    [InlineData(@"Sample\Meshes\meshes_360_final\trees\pine01.spt", 3, 2, 0.35f, 2u, 90f, 0.14f)]
    [InlineData(@"Sample\Meshes\meshes_360_final\trees\whiteoak01.spt", 4, 3, 0.45f, 2u, 50f, 0.099999994f)]
    [InlineData(@"Sample\Meshes\meshes_360_final\trees\wastelandshrub01.spt", 4, 4, 0.31f, 2u, 400f, 0.11f)]
    public void Parse_RealSamples_CarryTokensUsedByGeometryBuilder(
        string relativePath,
        int branchCount,
        int leafCount,
        float spacing3007,
        uint mode3008,
        float firstBranch6012,
        float firstLeafCorner1X)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = SampleFileFixture.FindSamplePath(relativePath);
        Assert.SkipWhen(path is null, "Missing sample: " + relativePath);

        var model = SptFile.Parse(File.ReadAllBytes(path!));

        Assert.Equal(branchCount, model.Branches.Count);
        Assert.Equal(leafCount, model.Leaves.Count);
        Assert.NotNull(model.LeafTable);
        Assert.Equal(spacing3007, model.LeafTable!.Float3007, 5);
        Assert.Equal(mode3008, model.LeafTable.UInt3008);
        Assert.Equal(firstBranch6012, model.Branches[0].Float6012, 5);
        Assert.Equal(firstLeafCorner1X, model.Leaves[0].Corner1.X, 5);
    }

    [Fact]
    public void Parse_LeafTableDefaults_MatchRuntimeConstructorWhenTokensAreOmitted()
    {
        var model = SptFile.Parse(BuildLeafTableOnlySpt((3001u, 1u)));

        Assert.Equal(0.75f, model.LeafSize);
        Assert.NotNull(model.LeafTable);
        Assert.Equal(1u, model.LeafTable!.UInt3001);
        Assert.Equal(0.8f, model.LeafTable.Float3002);
        Assert.Equal(0.5f, model.LeafTable!.Float3007);
        Assert.Equal(2u, model.LeafTable.UInt3008);
        Assert.Equal(1, model.LeafTable.Byte3009);
        Assert.Equal(1f, model.LeafTable.Float3010);
    }

    [Fact]
    public void Parse_LeafTable_ExplicitZeroSpacingStaysZero()
    {
        var model = SptFile.Parse(BuildLeafTableOnlySpt((3007u, 0f)));

        Assert.NotNull(model.LeafTable);
        Assert.Equal(0f, model.LeafTable!.Float3007);
        Assert.Equal(2u, model.LeafTable.UInt3008);
    }

    [Fact]
    public void Parse_LodSection_PreservesBranchAndLeafLevelContracts()
    {
        var bytes = new List<byte>(BuildLeafTableOnlySpt((3007u, 0.5f)));

        static void U32(List<byte> dst, uint value)
        {
            dst.AddRange(BitConverter.GetBytes(value));
        }

        static void F32(List<byte> dst, float value)
        {
            dst.AddRange(BitConverter.GetBytes(value));
        }

        U32(bytes, 9000); // outer begin
        U32(bytes, 9005); // branch/leaf LOD contract begin
        U32(bytes, 9007);
        U32(bytes, 4); // branch levels
        U32(bytes, 9008);
        F32(bytes, 0.25f); // branch far keep fraction
        U32(bytes, 9010);
        F32(bytes, 0.2f); // leaf size increase
        U32(bytes, 9011);
        U32(bytes, 3); // leaf levels
        U32(bytes, 9012);
        F32(bytes, 0.9f); // branch near keep fraction
        U32(bytes, 9013);
        F32(bytes, -0.1f);
        U32(bytes, 9014);
        F32(bytes, 0.075f);
        U32(bytes, 9006); // contract end
        U32(bytes, 9001); // outer end

        var model = SptFile.Parse([.. bytes]);

        Assert.NotNull(model.Lod);
        Assert.Equal(4, model.Lod!.NumBranchLods);
        Assert.Equal(3, model.Lod.NumLeafLods);
        Assert.Equal(0.9f, model.Lod.BranchNearFraction);
        Assert.Equal(0.25f, model.Lod.BranchFarFraction);
        Assert.Equal(0.2f, model.Lod.LeafLodSizeIncrease);
        Assert.Equal(-0.1f, model.Lod.BranchDemotionRandomness);
        Assert.Equal(0.075f, model.Lod.BranchGuaranteeFraction);
    }

    [Fact]
    public void Parse_LodSection_AuthoredZeroLeafSizeIncreaseUsesSdkFallback()
    {
        var bytes = new List<byte>(BuildLeafTableOnlySpt((3007u, 0.5f)));

        static void U32(List<byte> dst, uint value)
        {
            dst.AddRange(BitConverter.GetBytes(value));
        }

        static void F32(List<byte> dst, float value)
        {
            dst.AddRange(BitConverter.GetBytes(value));
        }

        U32(bytes, 9000);
        U32(bytes, 9005);
        U32(bytes, 9007);
        U32(bytes, 2);
        U32(bytes, 9010);
        F32(bytes, 0f);
        U32(bytes, 9011);
        U32(bytes, 2);
        U32(bytes, 9012);
        F32(bytes, 1f);
        U32(bytes, 9006);
        U32(bytes, 9001);

        var model = SptFile.Parse([.. bytes]);

        Assert.NotNull(model.Lod);
        Assert.Equal(0.1f, model.Lod!.LeafLodSizeIncrease);
    }

    [Fact]
    public void TryParse_NonSptBytes_ReturnsNull()
    {
        Assert.Null(SptFile.TryParse(Encoding.ASCII.GetBytes("this is not a speedtree file at all")));
    }

    [Fact]
    public void Parse_FrondSection_ReadsEnabledAndLevel()
    {
        // Post-tree 13000 section (CFrondEngine::Parse): 13002 = level (int), 13007 = enabled (ONE byte);
        // other tokens skipped by payload shape; 13001 terminates.
        var buf = new List<byte>();
        buf.AddRange(BuildLeafTableOnlySpt((3007u, 0.5f)));
        buf.AddRange(BitConverter.GetBytes(13000u));
        buf.AddRange(BitConverter.GetBytes(13002u));
        buf.AddRange(BitConverter.GetBytes(2u)); // frond level 2
        buf.AddRange(BitConverter.GetBytes(13003u));
        buf.AddRange(BitConverter.GetBytes(4u)); // skipped int
        buf.AddRange(BitConverter.GetBytes(13007u));
        buf.Add(1); // enabled byte
        buf.AddRange(BitConverter.GetBytes(13010u));
        buf.AddRange(BitConverter.GetBytes(0.25f)); // skipped float
        buf.AddRange(BitConverter.GetBytes(13001u));

        var model = SptFile.Parse([.. buf]);

        Assert.NotNull(model.Frond);
        Assert.True(model.Frond!.Enabled);
        Assert.Equal(2, model.Frond.Level);
    }

    [Fact]
    public void Parse_NoFrondSection_DefaultsDisabled()
    {
        var model = SptFile.Parse(BuildLeafTableOnlySpt((3007u, 0.5f)));

        Assert.Null(model.Frond); // CFrondEngine ctor default: disabled
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

    private static byte[] BuildLeafTableOnlySpt(params (uint Token, object Value)[] entries)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, true);
        WriteToken(1000);
        WriteString("__IdvSpt_02_");
        WriteToken(1004);
        foreach (var (token, value) in entries)
        {
            WriteToken(token);
            switch (value)
            {
                case uint u:
                    WriteToken(u);
                    break;
                case float f:
                    writer.Write(f);
                    break;
                case byte b:
                    writer.Write(b);
                    break;
                case Vector3 v:
                    writer.Write(v.X);
                    writer.Write(v.Y);
                    writer.Write(v.Z);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entries), value, "Unsupported test SPT value.");
            }
        }

        WriteToken(1005);
        WriteToken(1001);
        return ms.ToArray();

        void WriteToken(uint token)
        {
            writer.Write(token);
        }

        void WriteString(string value)
        {
            writer.Write((uint)value.Length);
            writer.Write(Encoding.ASCII.GetBytes(value));
        }
    }
}