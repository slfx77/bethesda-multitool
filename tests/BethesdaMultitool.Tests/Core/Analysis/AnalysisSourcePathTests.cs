using BethesdaMultitool.Core.Analysis;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Analysis;

/// <summary>
///     The file-versus-install-directory distinction that lets a classic game open in the Single
///     File Analysis tab. Every assertion here is against the real filesystem via a temp
///     directory, because the whole point of the helper is that <see cref="FileInfo" /> answers
///     these questions wrongly (or by throwing) for a directory.
/// </summary>
public sealed class AnalysisSourcePathTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AnalysisSourcePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bmt-srcpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "install.jar");
        File.WriteAllBytes(_file, new byte[] { 1, 2, 3, 4, 5 });
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }

    [Fact]
    public void IsMappable_File_IsTrue() => Assert.True(AnalysisSourcePath.IsMappable(_file));

    [Fact]
    public void IsMappable_Directory_IsFalse() => Assert.False(AnalysisSourcePath.IsMappable(_dir));

    [Fact]
    public void IsInstallDirectory_Directory_IsTrue() =>
        Assert.True(AnalysisSourcePath.IsInstallDirectory(_dir));

    [Fact]
    public void IsInstallDirectory_File_IsFalse() =>
        Assert.False(AnalysisSourcePath.IsInstallDirectory(_file));

    /// <summary>The independently known length is the fixture's five written bytes.</summary>
    [Fact]
    public void SizeOf_File_IsTheByteCount() => Assert.Equal(5L, AnalysisSourcePath.SizeOf(_file));

    /// <summary>
    ///     A directory reports zero rather than throwing. <c>new FileInfo(dir).Length</c> raises
    ///     <see cref="FileNotFoundException" />, which is exactly what blocked the GUI.
    /// </summary>
    [Fact]
    public void SizeOf_Directory_IsZeroAndDoesNotThrow()
    {
        Assert.Equal(0L, AnalysisSourcePath.SizeOf(_dir));
        Assert.Throws<FileNotFoundException>(() => new FileInfo(_dir).Length);
    }

    [Fact]
    public void DisplayName_File_IsTheFileName() =>
        Assert.Equal("install.jar", AnalysisSourcePath.DisplayName(_file));

    [Fact]
    public void DisplayName_Directory_IsTheLeafName() =>
        Assert.Equal(Path.GetFileName(_dir), AnalysisSourcePath.DisplayName(_dir));

    /// <summary>
    ///     Install roots often arrive with a trailing separator, where
    ///     <see cref="Path.GetFileName(string)" /> alone returns an empty string.
    /// </summary>
    [Fact]
    public void DisplayName_DirectoryWithTrailingSeparator_StillNamesTheLeaf()
    {
        var withSeparator = _dir + Path.DirectorySeparatorChar;
        Assert.Equal("", Path.GetFileName(withSeparator));
        Assert.Equal(Path.GetFileName(_dir), AnalysisSourcePath.DisplayName(withSeparator));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingPath_IsNeitherMappableNorADirectory(string? path)
    {
        Assert.False(AnalysisSourcePath.IsMappable(path));
        Assert.False(AnalysisSourcePath.IsInstallDirectory(path));
        Assert.Equal(0L, AnalysisSourcePath.SizeOf(path));
        Assert.Equal("", AnalysisSourcePath.DisplayName(path));
    }

    [Fact]
    public void NonExistentPath_IsNeitherMappableNorADirectory()
    {
        var missing = Path.Combine(_dir, "no-such-thing");
        Assert.False(AnalysisSourcePath.IsMappable(missing));
        Assert.False(AnalysisSourcePath.IsInstallDirectory(missing));
        Assert.Equal(0L, AnalysisSourcePath.SizeOf(missing));
    }
}
