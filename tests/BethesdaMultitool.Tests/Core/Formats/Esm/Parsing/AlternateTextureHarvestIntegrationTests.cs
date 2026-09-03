using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
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
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class AlternateTextureHarvestIntegrationTests
{
    [Fact]
    public async Task Fnv_AtomicWranglerReference_RetainsLiteralModsIdentityThroughWorldBake()
    {
        const uint referenceFormId = 0x00177F9B;
        const uint baseFormId = 0x00177F96;
        const uint cellFormId = 0x000DDF00;
        const uint textureSetFormId = 0x0016A885;
        const string shapeName = "BB04:13";

        var esm = RealAssetPaths.Masters.FalloutNv();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);
        var raw = Assert.Single(result.Records.AlternateTexturesByFormId[baseFormId]);
        Assert.Equal(shapeName, raw.ShapeName);
        Assert.Equal(textureSetFormId, raw.TextureSetFormId);
        Assert.Equal(1, raw.Index);

        var txst = Assert.Single(result.Records.TextureSets.Where(record => record.FormId == textureSetFormId));
        Assert.Equal("NVBillboardAtomicWrangler", txst.EditorId);
        Assert.Equal(@"clutter\billboards\AtomicWrangler_Billboard.dds", txst.DiffuseTexture,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"clutter\billboards\AtomicWrangler_Billboard_n.dds", txst.NormalTexture,
            StringComparer.OrdinalIgnoreCase);

        var world = global::BethesdaMultitool.WorldMapOverlayBuilder.BuildFromRecords(
            result.Records, esm);
        Assert.Equal(BethesdaGame.FalloutNewVegas, world.Game);
        Assert.True(world.PlacedRefs.TryGetEntry(referenceFormId, out var placed));
        Assert.Equal(cellFormId, placed.Cell.FormId);
        Assert.Equal(3, placed.Cell.GridX);
        Assert.Equal(11, placed.Cell.GridY);
        Assert.Equal(baseFormId, placed.Ref.BaseFormId);
        Assert.Equal(@"clutter\billboards\BillboardTallNV.NIF", placed.Ref.ModelPath,
            StringComparer.OrdinalIgnoreCase);

        var resolved = world.AlternateTexturesByFormId[baseFormId];
        Assert.True(resolved.Overrides.TryGetValue(shapeName, out var textureOverride));
        Assert.Equal(textureSetFormId, textureOverride.TextureSetFormId);
        Assert.Equal(1, textureOverride.Index);
        Assert.Equal(@"textures\clutter\billboards\AtomicWrangler_Billboard.dds",
            textureOverride.Diffuse, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"textures\clutter\billboards\AtomicWrangler_Billboard_n.dds",
            textureOverride.Normal, StringComparer.OrdinalIgnoreCase);

        var renderable = RenderableReference.TryBuild(
            placed.Ref, alternateTextures: resolved, game: world.Game);
        Assert.True(renderable.HasValue);
        Assert.Equal(referenceFormId, renderable.Value.FormId);
        Assert.Equal(baseFormId, renderable.Value.BaseFormId);
        Assert.Same(resolved, renderable.Value.AlternateTextures);
        var unskinned = RenderableReference.TryBuild(placed.Ref, game: world.Game);
        Assert.True(unskinned.HasValue);
        Assert.NotEqual(unskinned.Value.MeshId, renderable.Value.MeshId);
    }

    [Fact]
    public async Task Fnv_HarvestsBillboardAlternateTextures_WithDistinctTxstPerBase()
    {
        var esm = RealAssetPaths.Masters.FalloutNv();
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
