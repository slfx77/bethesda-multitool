using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     End-to-end smoke for the <c>MODS</c> harvest: parsing a real FNV plugin must populate
///     <c>RecordCollection.AlternateTexturesByFormId</c>, and the shared billboard shape
///     (<c>BB04:13</c> of <c>clutter\billboards\BillboardTallNV.NIF</c>) must map to multiple distinct
///     TXSTs — that difference is exactly what makes each billboard render its own vendor ad instead of
///     one shared default. Skipped when no FNV plugin is available.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class AlternateTextureHarvestIntegrationTests
{
    private static string? ResolveFalloutNvEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "FalloutNV.esm")))
        {
            return Path.Combine(root, "FalloutNV.esm");
        }

        string[] candidates =
        [
            @"Sample\ESM\pc_final\FalloutNV.esm",
            @"E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Fnv_HarvestsBillboardAlternateTextures_WithDistinctTxstPerBase()
    {
        var esm = ResolveFalloutNvEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var index = result.Records.AlternateTexturesByFormId;
        Assert.NotEmpty(index);

        // The BillboardTallNV.NIF "BB04:13" shape is re-skinned across many vendor STATs. If our harvest
        // works, that one shape name appears with several different TXST FormIDs across base objects.
        const string billboardShape = "BB04:13";
        var distinctTxsts = index.Values
            .SelectMany(entries => entries)
            .Where(e => string.Equals(e.ShapeName, billboardShape, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.TextureSetFormId)
            .Distinct()
            .ToList();

        Assert.True(distinctTxsts.Count >= 2,
            $"Expected the shared billboard shape '{billboardShape}' to map to multiple distinct TXSTs " +
            $"(one NIF, many ads); got {distinctTxsts.Count}. If 0, MODS is not being harvested.");
    }
}