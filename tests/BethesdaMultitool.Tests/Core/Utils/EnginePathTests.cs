using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Utils;

/// <summary>
///     The engine-path helpers must read a backslash-spelled archive path the same way on every
///     host — the pins below are exactly what <c>Path.GetFileName</c> gets wrong on Linux, where
///     <c>\</c> is an ordinary character.
/// </summary>
public sealed class EnginePathTests
{
    [Theory]
    [InlineData(@"meshes\a\b.nif", "b.nif", "b", @"meshes\a")]
    [InlineData("meshes/a/b.nif", "b.nif", "b", "meshes/a")]
    [InlineData(@"\WastelandShrub01.spt", "WastelandShrub01.spt", "WastelandShrub01", "")]
    [InlineData("globals.xml", "globals.xml", "globals", "")]
    [InlineData(@"textures\water\defaultwater_normal.dds", "defaultwater_normal.dds", "defaultwater_normal", @"textures\water")]
    [InlineData(@"dir\.hidden", ".hidden", "", "dir")]
    [InlineData(@"dir\trailing.", "trailing.", "trailing", "dir")]
    public void SplitsOnEitherSeparatorRegardlessOfHost(string path, string fileName, string stem, string directory)
    {
        Assert.Equal(fileName, EnginePath.FileName(path));
        Assert.Equal(stem, EnginePath.FileNameWithoutExtension(path));
        Assert.Equal(directory, EnginePath.DirectoryName(path));
    }

    [Fact]
    public void AgreesWithTheBclWhereTheBclSplitsOnBackslash()
    {
        // On Windows Path.GetFileName splits on both separators too, so the helper must agree with
        // it there — that agreement is what keeps the Windows suite byte-identical after callers
        // switch over. Elsewhere the BCL is the thing being worked around, so there is nothing to pin.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Path.GetFileName splits on '\\' only on Windows.");

        foreach (var path in new[] { @"meshes\a\b.nif", "meshes/a/b.nif", @"\x.spt", "y.xml", @"dir\.hidden", @"dir\trailing." })
        {
            Assert.Equal(Path.GetFileName(path), EnginePath.FileName(path));
            Assert.Equal(Path.GetFileNameWithoutExtension(path), EnginePath.FileNameWithoutExtension(path));
        }
    }
}
