using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Utils;

/// <summary>
///     An engine-spelled, differently cased relative path must reach the file the game itself would
///     open, on every host. On Windows the file system answers case; on Linux the helper's segment
///     walk does, and without it every probe below but the exact one is a miss.
/// </summary>
public sealed class HostPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hostpath-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ResolvesAnEngineSpelledPathHoweverTheHostCasesIt()
    {
        // Staged with host separators and MIXED case; every probe below is engine-spelled and
        // cased differently, which the game's own (DOS / Windows) file semantics accept.
        var directory = Path.Combine(_root, "Arena2");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Arch3d.bsa"), [1]);

        Assert.True(HostPath.FileExists(_root, @"ARENA2\ARCH3D.BSA"));
        Assert.True(HostPath.FileExists(_root, "arena2/arch3d.bsa"));
        Assert.False(HostPath.FileExists(_root, @"ARENA2\MAPS.BSA"));
        // A directory is not a file, whatever its case.
        Assert.False(HostPath.FileExists(_root, "ARENA2"));

        var resolved = HostPath.ResolveDirectory(_root, "ARENA2");
        Assert.True(Directory.Exists(resolved));
        Assert.Equal(directory, resolved, StringComparer.OrdinalIgnoreCase);

        // An absent directory resolves to the exact join, so a caller that tolerates one is unchanged.
        Assert.Equal(Path.Combine(_root, "missing"), HostPath.ResolveDirectory(_root, "missing"));
        Assert.Null(HostPath.TryResolveExisting(_root, @"ARENA2\missing.bsa"));

        Assert.Single(HostPath.EnumerateFiles(directory, "*.BSA"));
        Assert.Empty(HostPath.EnumerateFiles(directory, "*.snd"));
    }

    [Fact]
    public void ReSpellsOnlyTheSeparatorTheHostLacks()
    {
        var joined = HostPath.Combine(_root, @"a\b.txt");

        Assert.Equal(Path.Combine(_root, "a", "b.txt"), joined);
        Assert.Equal(Path.DirectorySeparatorChar == '\\' ? @"a\b.txt" : "a/b.txt", HostPath.FromEngine(@"a\b.txt"));
    }
}
