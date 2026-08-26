using BethesdaMultitool.CLI.Shared;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Shared;

/// <summary>
///     Tests for the shared file-or-directory dump discovery helper used by
///     `dmp game-time` (recursive, path-ordered) and `dmp formtype-census`
///     (top-level, LastWriteTimeUtc-ordered).
/// </summary>
public sealed class CliHelpersDiscoverDumpsTests : IDisposable
{
    private readonly string _root;

    public CliHelpersDiscoverDumpsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "btool-discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void FileInput_ReturnsThatSingleFileAsFullPath()
    {
        var file = Path.Combine(_root, "single.dmp");
        File.WriteAllBytes(file, [0x00]);

        var result = CliHelpers.DiscoverDumps(file, SearchOption.TopDirectoryOnly);

        Assert.NotNull(result);
        var only = Assert.Single(result);
        Assert.Equal(Path.GetFullPath(file), only);
    }

    [Fact]
    public void MissingPath_ReturnsNull()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Null(CliHelpers.DiscoverDumps(missing, SearchOption.AllDirectories));
    }

    [Fact]
    public void DirectoryInput_FiltersHangdumpsAndNonDmpFiles_OrdersByPath()
    {
        File.WriteAllBytes(Path.Combine(_root, "b.dmp"), [0x00]);
        File.WriteAllBytes(Path.Combine(_root, "a.dmp"), [0x00]);
        File.WriteAllBytes(Path.Combine(_root, "test_hangdump.dmp"), [0x00]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a dump");

        var result = CliHelpers.DiscoverDumps(_root, SearchOption.TopDirectoryOnly);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("a.dmp", Path.GetFileName(result[0]));
        Assert.Equal("b.dmp", Path.GetFileName(result[1]));
    }

    [Fact]
    public void DirectoryInput_SearchOptionControlsRecursion()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(_root, "top.dmp"), [0x00]);
        File.WriteAllBytes(Path.Combine(sub, "nested.dmp"), [0x00]);

        var topOnly = CliHelpers.DiscoverDumps(_root, SearchOption.TopDirectoryOnly);
        var recursive = CliHelpers.DiscoverDumps(_root, SearchOption.AllDirectories);

        Assert.NotNull(topOnly);
        var only = Assert.Single(topOnly);
        Assert.Equal("top.dmp", Path.GetFileName(only));
        Assert.NotNull(recursive);
        Assert.Equal(2, recursive.Count);
    }

    [Fact]
    public void DirectoryInput_OrderByLastWriteTime_SortsOldestCaptureFirst()
    {
        // Name order would put a_newer first; LastWriteTimeUtc order must win.
        var newer = Path.Combine(_root, "a_newer.dmp");
        var older = Path.Combine(_root, "b_older.dmp");
        File.WriteAllBytes(newer, [0x00]);
        File.WriteAllBytes(older, [0x00]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

        var result = CliHelpers.DiscoverDumps(
            _root, SearchOption.TopDirectoryOnly, orderByLastWriteTime: true);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("b_older.dmp", Path.GetFileName(result[0]));
        Assert.Equal("a_newer.dmp", Path.GetFileName(result[1]));
    }

    [Fact]
    public void EmptyDirectory_ReturnsEmptyList()
    {
        var result = CliHelpers.DiscoverDumps(_root, SearchOption.AllDirectories);

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
