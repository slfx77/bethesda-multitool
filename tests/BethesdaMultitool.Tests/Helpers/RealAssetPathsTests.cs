using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Synthetic coverage for the retail-asset locator. These run everywhere — the fixtures are
///     empty files in a temp directory, never real game data — because the locator is what decides
///     whether the opt-in suites find anything at all. A locator that silently resolves nothing
///     turns every real-asset test into a permanent skip that still reports green.
///     <para>
///         In <see cref="ProcessEnvironmentGroup" /> because the constructor points
///         <c>BETHESDA_TEST_DATA_ROOT</c> at a scratch directory for the lifetime of each test.
///         That variable is process-wide and is read at call time by
///         <see cref="RealAssetPaths.SteamGameFile" />, which ~40 real-asset test files depend on;
///         run in parallel with them, these tests would redirect their lookups at an empty folder
///         and turn genuine coverage into silent skips.
///     </para>
/// </summary>
[Collection(ProcessEnvironmentGroup.Name)]
public sealed class RealAssetPathsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bmt-assetpaths-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previousRoot =
        Environment.GetEnvironmentVariable(RealAssetPaths.RootVariable);

    public RealAssetPathsTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(RealAssetPaths.RootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RealAssetPaths.RootVariable, _previousRoot);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail an otherwise passing run.
        }
    }

    [Fact]
    public void OverrideRoot_ResolvesAFlatDirectoryOfMasters()
    {
        // The common shape: one scratch folder holding just the masters under test.
        var master = Path.Combine(_root, "Oblivion.esm");
        File.WriteAllBytes(master, [0x54, 0x45, 0x53, 0x34]);

        var resolved = RealAssetPaths.SteamGameFile("Oblivion", Path.Combine("Data", "Oblivion.esm"));

        Assert.Equal(master, resolved);
    }

    [Fact]
    public void OverrideRoot_AlsoResolvesAMirroredSteamLayout()
    {
        // The other shape: a copy that preserves <Game>\Data\<file>, so one root serves many games.
        var nested = Path.Combine(_root, "Oblivion", "Data");
        Directory.CreateDirectory(nested);
        var master = Path.Combine(nested, "Oblivion.esm");
        File.WriteAllBytes(master, [0x54, 0x45, 0x53, 0x34]);

        var resolved = RealAssetPaths.SteamGameFile("Oblivion", Path.Combine("Data", "Oblivion.esm"));

        Assert.Equal(master, resolved);
    }

    [Fact]
    public void MissingAsset_ResolvesToNullRatherThanAGuessedPath()
    {
        // Null is the contract callers skip on. Returning a plausible-but-absent path instead would
        // surface as a confusing File.Exists failure deep inside a test.
        Assert.Null(RealAssetPaths.SteamGameFile(
            "NoSuchGame", Path.Combine("Data", "NoSuchMaster.esm")));
    }

    [Fact]
    public void Directories_ResolveThroughTheMirroredLayoutToo()
    {
        var scripts = Path.Combine(_root, "Fallout 4", "Data", "Scripts");
        Directory.CreateDirectory(scripts);

        var resolved = RealAssetPaths.SteamGameDirectory("Fallout 4", Path.Combine("Data", "Scripts"));

        Assert.Equal(scripts, resolved);
    }

    [Fact]
    public void SkipMessage_NamesTheOverrideVariableSoASkippedRunSaysHowToEnableIt()
    {
        Assert.Contains(
            RealAssetPaths.RootVariable,
            RealAssetPaths.SkipMessage("Oblivion.esm"),
            StringComparison.Ordinal);
    }
}
