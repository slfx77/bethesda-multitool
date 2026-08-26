using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins how a record's MODL becomes an archive path. Archive entries are rooted at
///     <c>meshes\</c>, so anything that leaves a stray prefix in place turns into a lookup miss —
///     and a miss is not retried: the decoder writes a permanent negative for a mesh that ships
///     perfectly well, so the object never renders for the rest of the session.
/// </summary>
public sealed class ReferenceModelPathNormalizationTests
{
    [Theory]
    // The plain cases: bare path, already rooted, leading slash, forward slashes.
    [InlineData(@"setdressing\signage\Sign01.nif", @"meshes\setdressing\signage\Sign01.nif")]
    [InlineData(@"meshes\setdressing\Sign01.nif", @"meshes\setdressing\Sign01.nif")]
    [InlineData(@"\setdressing\Sign01.nif", @"meshes\setdressing\Sign01.nif")]
    [InlineData("setdressing/signage/Sign01.nif", @"meshes\setdressing\signage\Sign01.nif")]
    // Authored from the game folder instead of the archive root. Fallout 76 ships 26 of these;
    // before the Data\ strip they resolved to "meshes\Data\meshes\..." and cached as found=0.
    [InlineData(@"Data\meshes\setdressing\signage\SignClarksburg_patch01.nif",
        @"meshes\setdressing\signage\SignClarksburg_patch01.nif")]
    [InlineData(@"data\meshes\setdressing\signage\SignClarksburg_patch03_01.nif",
        @"meshes\setdressing\signage\SignClarksburg_patch03_01.nif")]
    // Data\ without the meshes\ segment still ends up rooted exactly once.
    [InlineData(@"Data\setdressing\Sign01.nif", @"meshes\setdressing\Sign01.nif")]
    [InlineData(@"\Data\setdressing\Sign01.nif", @"meshes\setdressing\Sign01.nif")]
    public void NormalizeModelPath_RootsEveryFormAtMeshes(string authored, string expected)
    {
        Assert.Equal(expected, ReferenceModelPath.Normalize(authored));
    }

    [Fact]
    public void NormalizeModelPath_LeavesSpeedTreeUnderTrees()
    {
        // .spt files live at the archive root under trees\, never under meshes\ — prefixing them
        // would miss every tree.
        Assert.StartsWith(
            @"trees\",
            ReferenceModelPath.Normalize(@"\WastelandShrub01.spt"),
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeModelPath_DoesNotEatADirectoryNamedDatabase()
    {
        // The strip must match the "data\" segment, not any name that merely starts with "data".
        Assert.Equal(
            @"meshes\database\Terminal01.nif",
            ReferenceModelPath.Normalize(@"database\Terminal01.nif"));
    }
}
