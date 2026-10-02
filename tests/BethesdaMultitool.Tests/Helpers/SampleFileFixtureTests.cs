using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

public sealed class SampleFileFixtureTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("bmt-sample-fixture-").FullName;

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, true)] // Full x64 output layout.
    [InlineData(7, true)] // Development adds a profile directory.
    [InlineData(12, true)]
    [InlineData(12, false)]
    public void FindSamplePath_FindsFilesAboveOutputDirectories(int depth, bool trailingSeparator)
    {
        var relative = Path.Combine("Sample", "fixture.bin");
        var expected = CreateFile(_scratch, relative);
        var output = CreateOutputDirectory(depth);
        if (trailingSeparator) output += Path.DirectorySeparatorChar;

        Assert.Equal(expected, SampleFileFixture.FindSamplePath(relative, output));
    }

    [Fact]
    public void FindSamplePath_PrefersTheNearestAncestor()
    {
        var relative = Path.Combine("Sample", "fixture.bin");
        CreateFile(_scratch, relative);
        var nearer = CreateOutputDirectory(3);
        var expected = CreateFile(nearer, relative);
        var output = Directory.CreateDirectory(Path.Combine(nearer, "bin")).FullName;

        Assert.Equal(expected, SampleFileFixture.FindSamplePath(relative, output));
    }

    [Fact]
    public void FindSamplePath_TriesMigratedSpellingAfterTheOriginalAtEachLevel()
    {
        var legacy = Path.Combine("Sample", "MemoryDump", "fixture.bin");
        var migrated = Path.Combine("Sample", "MemoryDumps", "fixture.bin");
        var expectedMigrated = CreateFile(_scratch, migrated);
        var output = CreateOutputDirectory(12);

        Assert.Equal(expectedMigrated, SampleFileFixture.FindSamplePath(legacy, output));

        var expectedOriginal = CreateFile(_scratch, legacy);
        Assert.Equal(expectedOriginal, SampleFileFixture.FindSamplePath(legacy, output));
    }

    [Fact]
    public void FindSamplePath_ReturnsNullWhenNoAncestorHasTheFile()
    {
        var relative = Path.Combine("Sample", Path.GetFileName(_scratch) + ".missing");

        Assert.Null(SampleFileFixture.FindSamplePath(relative, CreateOutputDirectory(12)));
    }

    private string CreateOutputDirectory(int depth)
    {
        var path = _scratch;
        for (var level = 0; level < depth; level++) path = Path.Combine(path, "output" + level);
        return Directory.CreateDirectory(path).FullName;
    }

    private static string CreateFile(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic fixture");
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_scratch, true);
    }
}
