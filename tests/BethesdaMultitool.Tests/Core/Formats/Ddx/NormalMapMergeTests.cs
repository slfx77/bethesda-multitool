using BethesdaMultitool.Core.Formats.Ddx;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Ddx;

/// <summary>
///     Pins the normal/specular pairing rules. These decide whether a converted Xbox 360 normal
///     map gets its authored specular mask in alpha or the merge's neutral gray fallback — and a
///     flat gray alpha renders every surface with a uniform 50% specular, which is what the
///     repacker shipped while it staged textures into a flat temp directory and the companion
///     lookup could never resolve.
/// </summary>
public class NormalMapMergeTests
{
    [Theory]
    [InlineData("textures\\landscape\\rock_n.ddx", true)]
    [InlineData("textures\\landscape\\rock_N.DDX", true)]
    [InlineData("rock_n.dds", true)]
    [InlineData("textures\\landscape\\rock.ddx", false)]
    [InlineData("textures\\landscape\\rock_s.ddx", false)]
    [InlineData("textures\\landscape\\rock_g.ddx", false)]
    public void IsNormalMapPath_MatchesTheUnderscoreNConvention(string path, bool expected)
    {
        Assert.Equal(expected, NormalMapMerge.IsNormalMapPath(path));
    }

    [Theory]
    [InlineData("textures\\landscape\\rock_n.ddx", "textures\\landscape\\rock_s.ddx")]
    [InlineData("textures\\landscape\\rock_n.dds", "textures\\landscape\\rock_s.dds")]
    [InlineData("rock_n.ddx", "rock_s.ddx")]
    public void ComputeSpecularPath_KeepsDirectoryAndExtension(string normal, string expected)
    {
        Assert.Equal(expected, NormalMapMerge.ComputeSpecularPath(normal));
    }

    [Fact]
    public void ComputeSpecularPath_NonNormalMap_ReturnsNull()
    {
        Assert.Null(NormalMapMerge.ComputeSpecularPath("textures\\landscape\\rock.ddx"));
    }

    [Fact]
    public void ComputeSpecularPath_RoundTripsBackToTheNormalItCameFrom()
    {
        // The repacker resolves the companion against archive paths, so the two derivations have
        // to agree exactly or the pair silently fails to meet.
        const string normal = "textures\\architecture\\repcon\\reprocket_n.ddx";
        var spec = NormalMapMerge.ComputeSpecularPath(normal);
        Assert.Equal("textures\\architecture\\repcon\\reprocket_s.ddx", spec);
        Assert.False(NormalMapMerge.IsNormalMapPath(spec!));
    }

    [Fact]
    public void IsAti2_DetectsTheFourCcTheEngineCannotLoad()
    {
        var dds = new byte[128];
        "DDS "u8.CopyTo(dds);
        "ATI2"u8.CopyTo(dds.AsSpan(84));
        Assert.True(NormalMapMerge.IsAti2(dds));

        "DXT5"u8.CopyTo(dds.AsSpan(84));
        Assert.False(NormalMapMerge.IsAti2(dds));

        Assert.False(NormalMapMerge.IsAti2(new byte[16]));
    }
}
