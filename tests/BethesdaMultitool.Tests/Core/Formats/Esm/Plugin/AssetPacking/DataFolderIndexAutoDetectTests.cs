using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Auto-detection tests for <see cref="DataFolderIndex" />. Secondary-data folders are
///     sometimes passed as the install root rather than the Data subfolder — both common PC
///     installs (FO3 GotY, FNV Steam) put BSAs under <c>&lt;root&gt;\Data\</c>, and Xbox 360
///     disc layouts put them under <c>&lt;root&gt;\&lt;GameName&gt;\Data\</c>. Without the
///     auto-descent in <see cref="DataFolderIndex.Build" />, loose-file paths get registered
///     with a <c>Data\</c> prefix that runtime requests never include, and BSAs are missed
///     entirely.
/// </summary>
public sealed class DataFolderIndexAutoDetectTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"assetpack-autodetect-{Guid.NewGuid():N}");

    public DataFolderIndexAutoDetectTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
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
    }

    [Fact]
    public void Build_DataFolderDirectlyPointed_IndexesLooseFilesByRuntimePath()
    {
        // Baseline shape: user already passes the Data folder. No descent needed; loose
        // files indexed under their runtime-shaped paths.
        var dataDir = Path.Combine(_scratchRoot, "Data");
        WriteLooseAsset(dataDir, @"meshes\test\thing.nif");
        WriteSentinelBsa(dataDir); // marks this as the Data folder

        using var index = new DataFolderIndex(dataDir, false);
        index.Build();

        Assert.True(index.TryResolveExact(@"meshes\test\thing.nif", out _));
    }

    [Fact]
    public void Build_PcInstallRoot_AutoDescendsIntoDataSubfolder()
    {
        // FO3 GotY / FNV Steam shape: BSAs at <root>\Data\*.bsa. Without auto-descent,
        // a request for "meshes\..." would miss the loose file (indexed as "Data\meshes\...")
        // and miss every BSA entirely (top-only enumeration at the root finds nothing).
        var installRoot = Path.Combine(_scratchRoot, "Fallout 3 goty");
        var dataDir = Path.Combine(installRoot, "Data");
        WriteLooseAsset(dataDir, @"meshes\paradisefalls\thing.nif");
        WriteSentinelBsa(dataDir);

        using var index = new DataFolderIndex(installRoot, false);
        index.Build();

        Assert.True(index.TryResolveExact(@"meshes\paradisefalls\thing.nif", out _));
    }

    [Fact]
    public void Build_Xbox360DiscLayout_AutoDescendsIntoNestedDataSubfolder()
    {
        // July 21 / August disc shape: <root>\FalloutNV\Data\*.bsa. Two-level descent
        // required; the loose-file enumerator and BSA enumerator both have to land on
        // the actual Data folder so paths match runtime requests.
        var discRoot = Path.Combine(_scratchRoot, "Fallout New Vegas (July 21, 2010)");
        var dataDir = Path.Combine(discRoot, "FalloutNV", "Data");
        WriteLooseAsset(dataDir, @"textures\common\thing.dds");
        WriteSentinelBsa(dataDir);

        using var index = new DataFolderIndex(discRoot, true);
        index.Build();

        Assert.True(index.TryResolveExact(@"textures\common\thing.dds", out _));
    }

    [Fact]
    public void Build_LooseOnlyFolderWithoutAnyBsa_StaysAtRoot()
    {
        // Manual asset folders (e.g. "Manually recovered textures BSA") may be flat with
        // assets directly at the top level. Auto-descent must NOT kick in when there are
        // no BSAs anywhere — staying at the user-supplied root preserves the existing
        // behavior for loose-only secondary data folders.
        var flatDir = Path.Combine(_scratchRoot, "loose-textures");
        WriteLooseAsset(flatDir, @"textures\flat\thing.dds");

        using var index = new DataFolderIndex(flatDir, false);
        index.Build();

        Assert.True(index.TryResolveExact(@"textures\flat\thing.dds", out _));
    }

    private void WriteLooseAsset(string dataFolder, string relativePath)
    {
        var absolutePath = Path.Combine(dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(absolutePath, [0x4E, 0x49, 0x46]);
    }

    private static void WriteSentinelBsa(string folder)
    {
        // The auto-detect probe only cares whether ANY *.bsa exists in the folder. An
        // empty file is enough; IndexBsas would catch the BsaExtractor parse failure
        // and skip it. We're not testing real BSA parsing here.
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "sentinel.bsa"), []);
    }
}