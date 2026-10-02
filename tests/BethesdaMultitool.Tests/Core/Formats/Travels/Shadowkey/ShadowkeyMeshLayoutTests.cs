using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The cut-2 additions to <see cref="ShadowkeyMesh" /> (plan slice 0, decision D8): the texture-header variant, the
///     section walk and the length rule that picks the variant of a <c>.zsk</c> sky payload. Records come from
///     <see cref="ShadowkeyTestBuilder" />, whose section sizes are stated independently below.
/// </summary>
public sealed class ShadowkeyMeshLayoutTests
{
    [Fact]
    public void Sections_TileTheRecord_AndMatchTheOraclesPinsOnTheGoldenRecord()
    {
        var bytes = ShadowkeyTestBuilder.GoldenAnimated();

        var mesh = ShadowkeyMesh.Parse(bytes, "golden");

        // The Python oracle's mesh_sections of the same bytes (shadowkey_cover.py selfcheck, golden.animated.sections).
        (string, int, int)[] expected =
        [
            ("header", 0, 14), ("positions", 14, 48), ("uvs", 62, 20), ("faces", 82, 24), ("texture-header", 106, 6),
            ("texels", 112, 16), ("sequence-count", 128, 2), ("sequences", 130, 12)
        ];
        Assert.Equal(expected, mesh.Sections.Select(static s => (s.Name, s.Offset, s.Length)));
        Assert.Equal(bytes.Length, mesh.Sections[^1].Offset + mesh.Sections[^1].Length);
        Assert.Equal(ShadowkeyTextureHeader.Counted, mesh.TextureHeader);
        Assert.Equal((112, 8), mesh.SkinTexelBlock(0));
        Assert.Equal((120, 8), mesh.SkinTexelBlock(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.SkinTexelBlock(2));
    }

    [Fact]
    public void Sections_FollowTheCounts_OnALargerRecord()
    {
        var record = new ShadowkeyTestBuilder.Record
        {
            Frames = 3,
            Positions = [.. Enumerable.Range(0, 3 * 5 * 3).Select(static v => (short)v)],
            Uvs = [0, 0, 256, 0, 256, 256, 0, 256, 128, 128, 64, 64],
            Faces = [0, 1, 2, 0, 1, 2, 0, 2, 3, 0, 2, 3, 1, 4, 2, 1, 4, 2],
            Skins = 3,
            Width = 4,
            Height = 2,
            Sequences = [(0, 1, 10), (1, 3, 5)]
        };
        var bytes = record.Build();

        var mesh = ShadowkeyMesh.Parse(bytes, "larger");

        var lengths = new[] { 14, 6 * 3 * 5, 4 * 6, 12 * 3, 6, 2 * 3 * 4 * 2, 2, 6 * 2 };
        Assert.Equal(lengths, mesh.Sections.Select(static s => s.Length));
        var offset = 0;
        foreach (var section in mesh.Sections)
        {
            Assert.Equal(offset, section.Offset);
            offset += section.Length;
        }

        Assert.Equal(bytes.Length, offset);
    }

    [Fact]
    public void TheUncountedHeader_Parses_AndTheCountedWalkRefusesIt()
    {
        var bytes = ShadowkeyTestBuilder.Zone.DefaultSky(counted: false);

        var mesh = ShadowkeyMesh.Parse(bytes, "interior.zsk", ShadowkeyTextureHeader.Uncounted);

        Assert.Equal(ShadowkeyTextureHeader.Uncounted, mesh.TextureHeader);
        Assert.Single(mesh.Textures.Skins);
        Assert.Equal(ShadowkeyTestBuilder.GoldenSkin1, mesh.Textures.Skins[0]);
        Assert.Equal(4, mesh.Section("texture-header").Length);
        // Control: the counted walk reads the width as the skin count and fails to tile.
        Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.Parse(bytes, "interior.zsk"));
    }

    [Fact]
    public void DetectTextureHeader_ChoosesTheLayoutThatTilesThePayload()
    {
        var counted = ShadowkeyTestBuilder.Zone.DefaultSky();
        var uncounted = ShadowkeyTestBuilder.Zone.DefaultSky(counted: false);

        Assert.Equal(ShadowkeyTextureHeader.Counted, ShadowkeyMesh.DetectTextureHeader(counted, "outdoor.zsk"));
        Assert.Equal(ShadowkeyTextureHeader.Uncounted, ShadowkeyMesh.DetectTextureHeader(uncounted, "interior.zsk"));
        // Neither layout tiles a payload with a stray byte, nor a truncated one.
        Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.DetectTextureHeader([.. counted, 0], "stray.zsk"));
        Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.DetectTextureHeader(counted.AsSpan(0, 20), "cut.zsk"));
    }

    [Fact]
    public void TheDefaultParse_IsTheCountedWalk()
    {
        var bytes = ShadowkeyTestBuilder.GoldenStatic();

        var byDefault = ShadowkeyMesh.Parse(bytes, "static");
        var counted = ShadowkeyMesh.Parse(bytes, "static", ShadowkeyTextureHeader.Counted);

        Assert.Equal(ShadowkeyTextureHeader.Counted, byDefault.TextureHeader);
        Assert.Equal(counted.Sections, byDefault.Sections);
        Assert.Equal(counted.Textures.Skins[0], byDefault.Textures.Skins[0]);
    }
}
