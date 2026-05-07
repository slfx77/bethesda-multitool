using FalloutXbox360Utils.CLI.Commands.Dmp;
using FalloutXbox360Utils.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

public sealed class DmpCompareSourceDiscoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "dmp-compare-discovery-tests", Guid.NewGuid().ToString("N"));

    public DmpCompareSourceDiscoveryTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void Discover_accepts_mixed_files_and_directories_while_filtering_unsupported_and_hangdump()
    {
        var rootDmp = WriteFile("build_a.dmp", new DateTime(2010, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var rootEsm = WriteFile("build_b.esm", new DateTime(2010, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        WriteFile("notes.txt", new DateTime(2010, 1, 4, 0, 0, 0, DateTimeKind.Utc));
        WriteFile("hangdump_ignored.dmp", new DateTime(2010, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        var explicitEsm = WriteFile("explicit.esm", new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var sources = DmpCompareSourceDiscovery.Discover([_tempDir, explicitEsm], recursive: false);

        Assert.Equal([explicitEsm, rootDmp, rootEsm], sources.Select(source => source.FilePath).ToList());
        Assert.Equal(AnalysisFileType.EsmFile, sources[0].FileType);
        Assert.Equal(AnalysisFileType.Minidump, sources[1].FileType);
        Assert.DoesNotContain(sources, source => Path.GetFileName(source.FilePath).Contains("hangdump"));
    }

    [Fact]
    public void Discover_only_recurses_when_requested()
    {
        var nestedDir = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedDmp = Path.Combine(nestedDir, "nested.dmp");
        File.WriteAllBytes(nestedDmp, [1]);

        var topLevel = DmpCompareSourceDiscovery.Discover([_tempDir], recursive: false);
        var recursive = DmpCompareSourceDiscovery.Discover([_tempDir], recursive: true);

        Assert.DoesNotContain(topLevel, source => source.FilePath == nestedDmp);
        Assert.Contains(recursive, source => source.FilePath == nestedDmp);
    }

    private string WriteFile(string fileName, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
