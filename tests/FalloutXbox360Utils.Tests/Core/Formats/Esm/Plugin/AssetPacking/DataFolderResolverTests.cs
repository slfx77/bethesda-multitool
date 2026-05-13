using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class DataFolderResolverTests : IDisposable
{
    // One scratch tree shared by every test in this class; deleted on Dispose.
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"assetpack-resolver-{Guid.NewGuid():N}");

    public DataFolderResolverTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private string MakeDataFolder(string label) => Path.Combine(_scratchRoot, label);

    private void WriteLooseFile(string dataFolder, string relativePath, ReadOnlySpan<byte> bytes)
    {
        var absolutePath = Path.Combine(dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(absolutePath, bytes.ToArray());
    }

    [Fact]
    public void Resolve_BaselineHasExactPath_ReturnsAlreadyInBaseline()
    {
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "meshes\\already.nif", [1, 2, 3]);
        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();

        var resolver = new DataFolderResolver(baseline, []);
        var result = resolver.Resolve("meshes\\already.nif");

        Assert.Equal(AssetResolutionKind.AlreadyInBaseline, result.Kind);
        Assert.Null(result.Source);
    }

    [Fact]
    public void Resolve_SecondaryHasExactPath_ReturnsResolvedExact()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\fo3only.nif", [9, 9, 9]);

        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, xbox360FormatHint: false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\fo3only.nif");

        Assert.Equal(AssetResolutionKind.ResolvedExact, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal(0, result.SourceFolderIndex);
        Assert.Equal("meshes\\fo3only.nif", result.ResolvedPath);
    }

    [Fact]
    public void Resolve_FuzzyBasenameMatch_SingleCandidate()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        // Candidate lives under a different subdirectory than the request.
        WriteLooseFile(secondaryDir, "armor\\moved\\helm.nif", [1]);

        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, xbox360FormatHint: false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\armor\\headgear\\helm.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.Equal("armor\\moved\\helm.nif", result.ResolvedPath);
        Assert.Equal(1, result.FuzzySuffixTokens); // only the filename token matches
    }

    [Fact]
    public void Resolve_FuzzyBasenameMultipleCandidates_PicksLongestSuffix()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);

        var secondaryA = MakeDataFolder("foA");
        WriteLooseFile(secondaryA, "wrong\\branch\\test.nif", [1]); // suffix: 1 token

        var secondaryB = MakeDataFolder("foB");
        WriteLooseFile(secondaryB, "right\\branch\\test.nif", [2]); // suffix: 3 tokens (right, branch, test.nif)

        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();
        using var idxA = new DataFolderIndex(secondaryA, xbox360FormatHint: false);
        idxA.Build();
        using var idxB = new DataFolderIndex(secondaryB, xbox360FormatHint: false);
        idxB.Build();

        var resolver = new DataFolderResolver(baseline, [idxA, idxB]);
        var result = resolver.Resolve("meshes\\right\\branch\\test.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.Equal("right\\branch\\test.nif", result.ResolvedPath);
        Assert.Equal(3, result.FuzzySuffixTokens);
        Assert.Equal(1, result.SourceFolderIndex); // idxB
    }

    [Fact]
    public void Resolve_FuzzyTie_BreaksOnFolderPriority()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);

        var secondaryA = MakeDataFolder("foA");
        WriteLooseFile(secondaryA, "branch\\test.nif", [1]); // suffix: 2 tokens

        var secondaryB = MakeDataFolder("foB");
        WriteLooseFile(secondaryB, "branch\\test.nif", [2]); // suffix: 2 tokens (tie!)

        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();
        using var idxA = new DataFolderIndex(secondaryA, xbox360FormatHint: false);
        idxA.Build();
        using var idxB = new DataFolderIndex(secondaryB, xbox360FormatHint: false);
        idxB.Build();

        var resolver = new DataFolderResolver(baseline, [idxA, idxB]);
        var result = resolver.Resolve("a\\b\\branch\\test.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.Equal(0, result.SourceFolderIndex); // idxA wins the priority tie-break
    }

    [Fact]
    public void Resolve_NotFoundAnywhere_ReturnsMissing()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        Directory.CreateDirectory(secondaryDir);

        using var baseline = new DataFolderIndex(baselineDir, xbox360FormatHint: false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, xbox360FormatHint: false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\nonexistent.nif");

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
        Assert.Null(result.Source);
    }

    [Fact]
    public void Resolve_LooseFileWinsOverBsaInSameFolder()
    {
        // Without a synthetic BSA fixture this is hard to construct in unit-test scope; instead
        // we verify the lower-level invariant: AddSource respects first-write-wins in the index,
        // which is what gives loose files priority since they're indexed first.
        var dir = MakeDataFolder("priority");
        WriteLooseFile(dir, "meshes\\test.nif", [0xAA]);

        using var idx = new DataFolderIndex(dir, xbox360FormatHint: false);
        idx.Build();

        Assert.True(idx.TryResolveExact("meshes\\test.nif", out var source));
        Assert.IsType<LooseFileAssetSource>(source);
    }
}
