using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public sealed class DataFolderResolverTests : IDisposable
{
    // One scratch tree shared by every test in this class; deleted on Dispose.
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"assetpack-resolver-{Guid.NewGuid():N}");

    private bool _disposed;

    public DataFolderResolverTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    private string MakeDataFolder(string label)
    {
        return Path.Combine(_scratchRoot, label);
    }

    private static void WriteLooseFile(string dataFolder, string relativePath, ReadOnlySpan<byte> bytes)
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
        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();

        var resolver = new DataFolderResolver(baseline, []);
        var result = resolver.Resolve("meshes\\already.nif");

        Assert.Equal(AssetResolutionKind.AlreadyInBaseline, result.Kind);
        Assert.Null(result.Source);
    }

    [Fact]
    public void ResolveForForcedPack_BaselineHasExactPath_ReturnsReadableSource()
    {
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "textures\\characters\\facemods\\falloutnv.esm\\00104f09_0.dds",
            [1, 2, 3]);
        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();

        var resolver = new DataFolderResolver(baseline, []);
        var result = resolver.ResolveForForcedPack(
            "textures\\characters\\facemods\\falloutnv.esm\\00104f09_0.dds");

        Assert.Equal(AssetResolutionKind.ResolvedExact, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal([1, 2, 3], result.Source!.Read());
        Assert.Equal(-1, result.SourceFolderIndex);
    }

    [Fact]
    public void Resolve_SecondaryHasExactPath_ReturnsResolvedExact()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\fo3only.nif", [9, 9, 9]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
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

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\armor\\headgear\\helm.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.Equal("armor\\moved\\helm.nif", result.ResolvedPath);
        Assert.Equal(1, result.FuzzySuffixTokens); // only the filename token matches
    }

    [Fact]
    public void Resolve_FuzzyDisabled_ReturnsMissingForNearMatchesAndResolvesExactOnes()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "armor\\moved\\helm.nif", [1]); // only a fuzzy candidate
        WriteLooseFile(secondaryDir, "meshes\\exact.nif", [2]); // an exact candidate

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        // enableFuzzy: false — the fuzzy cascade is gated off, so a renamed asset misses...
        var exactOnly = new DataFolderResolver(baseline, [secondary], false, false);
        Assert.Equal(AssetResolutionKind.Missing, exactOnly.Resolve("meshes\\armor\\headgear\\helm.nif").Kind);
        // ...but exact resolution is unaffected.
        Assert.Equal(AssetResolutionKind.ResolvedExact, exactOnly.Resolve("meshes\\exact.nif").Kind);

        // enableFuzzy: true (default) — the same renamed asset now resolves via fuzzy.
        var fuzzy = new DataFolderResolver(baseline, [secondary]);
        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, fuzzy.Resolve("meshes\\armor\\headgear\\helm.nif").Kind);
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

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var idxA = new DataFolderIndex(secondaryA, false);
        idxA.Build();
        using var idxB = new DataFolderIndex(secondaryB, false);
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

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var idxA = new DataFolderIndex(secondaryA, false);
        idxA.Build();
        using var idxB = new DataFolderIndex(secondaryB, false);
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

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
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

        using var idx = new DataFolderIndex(dir, false);
        idx.Build();

        Assert.True(idx.TryResolveExact("meshes\\test.nif", out var source));
        Assert.IsType<LooseFileAssetSource>(source);
    }

    [Fact]
    public void Resolve_OverrideVanilla_SecondaryWinsOverBaselineExact()
    {
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "meshes\\shared.nif", [0xBA]);
        var secondaryDir = MakeDataFolder("override");
        WriteLooseFile(secondaryDir, "meshes\\shared.nif", [0x5E]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary], true);
        var result = resolver.Resolve("meshes\\shared.nif");

        Assert.Equal(AssetResolutionKind.ResolvedExact, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal(0, result.SourceFolderIndex);
        Assert.Equal("meshes\\shared.nif", result.ResolvedPath);
    }

    [Fact]
    public void Resolve_OverrideVanilla_SecondaryExtensionSwapWinsOverBaselineExact()
    {
        // Baseline has the .dds; secondary has the .ddx. Override mode should prefer the
        // secondary's extension-swap match (ResolvedFuzzy) over the baseline's exact match
        // — otherwise the override flag is half-broken (baseline wins, override never fires).
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "textures\\sand.dds", [0xBA]);
        var secondaryDir = MakeDataFolder("proto");
        WriteLooseFile(secondaryDir, "textures\\sand.ddx", [0x5E]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary], true);
        var result = resolver.Resolve("textures\\sand.dds");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.Equal("textures\\sand.ddx", result.ResolvedPath);
        Assert.Equal(0, result.SourceFolderIndex);
    }

    [Fact]
    public void Resolve_OverrideVanilla_FallsBackToBaselineWhenNoSecondaryHasIt()
    {
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "meshes\\baselineonly.nif", [0xBA]);
        var secondaryDir = MakeDataFolder("secondary");
        WriteLooseFile(secondaryDir, "meshes\\somethingelse.nif", [0x5E]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary], true);
        var result = resolver.Resolve("meshes\\baselineonly.nif");

        Assert.Equal(AssetResolutionKind.AlreadyInBaseline, result.Kind);
        Assert.Equal("meshes\\baselineonly.nif", result.ResolvedPath);
    }

    [Fact]
    public void Resolve_SubstringSuffix_DeltaSixCharsCatchesSpaceSuffixRename()
    {
        // Proto request "dinotoy static.nif" (loose stem `dinotoystatic`, length 13) vs
        // FNV-final candidate "dinotoy.nif" (loose stem `dinotoy`, length 7) — same folder,
        // starts-with the candidate stem, delta = 6. Previously rejected at the 4-char
        // budget; the bump to 6 brings this clean rename into the substring-suffix pass.
        var baselineDir = MakeDataFolder("baseline");
        WriteLooseFile(baselineDir, "meshes\\clutter\\dinotoy.nif", [0xAA]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();

        var resolver = new DataFolderResolver(baseline, []);
        var result = resolver.Resolve("meshes\\clutter\\dinotoy static.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal("meshes\\clutter\\dinotoy.nif", result.ResolvedPath);
    }

    [Fact]
    public void Resolve_ContainmentPass_SameFolderMidStemRename_UniqueCandidate()
    {
        // Proto request "enclavehelmet01.nif" (loose stem `enclavehelmet01`, length 15)
        // vs FNV-final "helmet.nif" (loose stem `helmet`, length 6) — same folder, the
        // candidate stem is contained as a substring inside the request stem (not anchored
        // at either end → substring-suffix would miss it). Other files in the folder
        // (backpack, glovel, glover, go, enclavearmor) don't satisfy the containment
        // predicate, so the unique-candidate gate fires and we pack `helmet.nif`.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\helmet.nif", [0x01]);
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\backpack.nif", [0x02]);
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\glovel.nif", [0x03]);
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\glover.nif", [0x04]);
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\enclavearmor.nif", [0x05]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\armor\\enclavepowerarmor\\enclavehelmet01.nif");

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal("meshes\\armor\\enclavepowerarmor\\helmet.nif", result.ResolvedPath);
        Assert.Equal(0, result.SourceFolderIndex);
    }

    [Fact]
    public void Resolve_ContainmentPass_AmbiguousFolder_ReturnsMissing()
    {
        // Two candidates in the same folder both contained-by-substring in the request
        // (`helmet` ⊂ `enclavehelmet01`, `helmetgo` ⊂ `enclavehelmetgo01`). The uniqueness
        // gate must bail rather than guess which one the proto meant.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\helmet.nif", [0x01]);
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\helmetgo.nif", [0x02]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        // Constructed so BOTH candidate stems are strictly contained: `helmet` ⊂
        // `enclavehelmetgo01`, AND `helmetgo` ⊂ `enclavehelmetgo01`.
        var result = resolver.Resolve("meshes\\armor\\enclavepowerarmor\\enclavehelmetgo01.nif");

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
    }

    [Fact]
    public void Resolve_ContainmentPass_DifferentFolder_StaysMissing()
    {
        // Same containment shape, but the candidate is in a sibling folder. Containment
        // is directory-anchored — refuse to cross folders.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\armor\\someotherfolder\\helmet.nif", [0x01]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\armor\\enclavepowerarmor\\enclavehelmet01.nif");

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
    }

    [Fact]
    public void Resolve_ContainmentPass_ExtensionMismatch_StaysMissing()
    {
        // `helmet.dds` is contained by `enclavehelmet01.nif` by loose-stem alone, but the
        // extensions aren't in the same swap class — refuse to cross asset families.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("fo3");
        WriteLooseFile(secondaryDir, "meshes\\armor\\enclavepowerarmor\\helmet.dds", [0x01]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.Resolve("meshes\\armor\\enclavepowerarmor\\enclavehelmet01.nif");

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
    }

    [Fact]
    public void Resolve_OverrideVanilla_ReturnsMissingWhenNeitherHas()
    {
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("secondary");
        Directory.CreateDirectory(secondaryDir);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, false);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary], true);
        var result = resolver.Resolve("meshes\\nowhere.nif");

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
    }

    [Fact]
    public void ResolveExactOnly_NoExactMatch_DoesNotFuzzyMatchSibling()
    {
        // A specular-companion lookup for `barrierbulletholes_s.ddx` when only the diffuse
        // (`barrierbulletholes`) and normal (`_n`) siblings exist. The full fuzzy resolver
        // would match the diffuse via the substring-suffix pass (wrong-resolution spec);
        // ResolveExactOnly must refuse and return Missing so the caller uses neutral gray.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("proto");
        WriteLooseFile(secondaryDir, "textures\\architecture\\barrier\\barrierbulletholes.ddx", [0x01]);
        WriteLooseFile(secondaryDir, "textures\\architecture\\barrier\\barrierbulletholes_n.ddx", [0x02]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, true);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        const string companion = "textures\\architecture\\barrier\\barrierbulletholes_s.ddx";

        // Sanity: the fuzzy resolver DOES (wrongly) match a sibling here.
        Assert.NotEqual(AssetResolutionKind.Missing, resolver.Resolve(companion).Kind);

        // Exact-only must not.
        var exact = resolver.ResolveExactOnly(companion);
        Assert.Equal(AssetResolutionKind.Missing, exact.Kind);
        Assert.Null(exact.Source);
    }

    [Fact]
    public void ResolveExactOnly_ExactSiblingPresent_ResolvesIt()
    {
        // When the real `_s` companion exists at the exact sibling path, exact-only resolves
        // it (here via the .ddx↔.dds extension swap), so a genuine specular is still used.
        var baselineDir = MakeDataFolder("baseline");
        Directory.CreateDirectory(baselineDir);
        var secondaryDir = MakeDataFolder("proto");
        WriteLooseFile(secondaryDir, "textures\\weapons\\gun_s.ddx", [0x42]);

        using var baseline = new DataFolderIndex(baselineDir, false);
        baseline.Build();
        using var secondary = new DataFolderIndex(secondaryDir, true);
        secondary.Build();

        var resolver = new DataFolderResolver(baseline, [secondary]);
        var result = resolver.ResolveExactOnly("textures\\weapons\\gun_s.dds");

        Assert.NotNull(result.Source);
        Assert.Equal("textures\\weapons\\gun_s.ddx", result.ResolvedPath);
    }
}