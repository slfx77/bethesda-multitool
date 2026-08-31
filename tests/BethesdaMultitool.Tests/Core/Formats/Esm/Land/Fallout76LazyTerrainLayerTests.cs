using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Fallout 76 has no in-CELL BTXT/ATXT — its land textures live in the external <c>.btd</c>, and
///     <c>BtdTerrainInjector</c> rebuilds them as BTXT/ATXT-shaped layers so the ordinary terrain
///     shader and the 2D blitters consume them unchanged. Those layers became LAZY on 2026-08-26 to
///     recover 1,250 MB, and this asserts the lazy route still yields the same terrain.
///     <para>
///         Without this, "the layers are lazy" and "the layers are gone" look identical from the
///         outside: terrain still draws, still has correct heights, and simply renders untextured.
///         Nothing in the default suite can see that, because it needs the real BTD.
///     </para>
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class Fallout76LazyTerrainLayerTests
{
    [Fact]
    public async Task Appalachia_Cells_Materialize_Real_Texture_Layers()
    {
        var esm = RealAssetPaths.Masters.SeventySix();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "SeventySix.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 76).");

        var result = await RealAssetEsmCache.LoadAsync(esm!, TestContext.Current.CancellationToken);

        var gridded = result.Records.Cells
            .Where(c => !c.IsInterior && c is { GridX: not null, GridY: not null })
            .ToList();
        Assert.True(gridded.Count > 1000, $"expected Appalachia's gridded cells; got {gridded.Count}");

        // The cheap predicate must find layers WITHOUT decoding — that is the whole point of the
        // side flag, and it is what WorldSpatialIndex/the 2D map ask per cell.
        var claimLayers = gridded.Where(c => c.LandVisualData?.HasTextureLayers == true).ToList();
        Assert.True(claimLayers.Count > 500,
            $"only {claimLayers.Count} of {gridded.Count} gridded cells report terrain layers; " +
            "the BTD injector attached no layer providers");

        // And materializing must actually produce them. A provider that returns empty would leave
        // terrain drawn, correctly shaped, and untextured.
        var sampled = claimLayers.Take(40).ToList();
        var withLayers = sampled.Count(c => c.LandVisualData!.TextureLayers.Count > 0);
        Assert.True(withLayers == sampled.Count,
            $"{sampled.Count - withLayers} of {sampled.Count} cells claimed layers but materialized " +
            "none — HasTextureLayers and TextureLayers disagree, which renders as untextured terrain");

        // The layers must carry real LTEX FormIds, or the resolver has nothing to look up.
        var resolvable = sampled
            .SelectMany(c => c.LandVisualData!.TextureLayers)
            .Count(l => l.TextureFormId != 0);
        Assert.True(resolvable > 0, "no layer carried a non-zero LTEX FormId");
    }

    [Fact]
    public async Task Appalachia_Ltex_Resolves_To_A_Material_Path_Not_A_Texture()
    {
        // Pins WHY FO76 terrain currently renders untextured, so the cause is not re-diagnosed as a
        // regression in whatever changed most recently.
        //
        // FO76 (like FO4/Starfield) has no LTEX -> TNAM -> TXST chain: LTEX names a MATERIAL
        // (.bgsm / .mat) whose diffuse lives in a material database. LandscapeTexturePathResolver
        // hands that path back verbatim, and TerrainTextureResolver12 has no material resolver — so
        // Resolve() falls through to EngineDefault and every cell draws the default ground.
        // Wiring a material resolver into the terrain path is the fix; nothing about the lazy layer
        // route above affects it.
        var esm = RealAssetPaths.Masters.SeventySix();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null, "SeventySix.esm not found.");

        var result = await RealAssetEsmCache.LoadAsync(esm!, TestContext.Current.CancellationToken);

        var ltex = result.Records.LandTextures;
        Assert.True(ltex.Count > 0, "FO76 exposed no LTEX records at all");

        // No LTEX should carry a TXST link (the FNV/Skyrim shape); they should carry material paths.
        var withTextureSet = ltex.Count(l => l.TextureSetFormId is not null);
        var withMaterial = ltex.Count(l => !string.IsNullOrWhiteSpace(l.MaterialPath));

        Assert.True(withMaterial > 0,
            $"expected FO76 LTEX to name materials; {withMaterial} of {ltex.Count} did " +
            $"({withTextureSet} carried a TXST link instead)");

        // NifGpuTextureResolver already turns a material path into its diffuse, so the machinery the
        // terrain path needs largely exists — the open question is why the chain does not land, not
        // whether a resolver has to be written from scratch.
        Assert.All(
            ltex.Where(l => !string.IsNullOrWhiteSpace(l.MaterialPath)),
            l => Assert.False(
                l.MaterialPath!.EndsWith(".dds", StringComparison.OrdinalIgnoreCase),
                "an LTEX material path that is really a .dds would mean the material indirection is " +
                "absent for this record, and the terrain resolver could consume it directly"));
    }
}
