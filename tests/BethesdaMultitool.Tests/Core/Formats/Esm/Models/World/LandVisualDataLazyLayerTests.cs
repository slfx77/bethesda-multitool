using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.World;

/// <summary>
///     Guards the lazy BTXT/ATXT layer route added for FO76/Starfield BTD terrain, where the eager
///     sweep retained 1,250 MB on Appalachia — 18% of the whole post-load managed heap.
///     <para>
///         The load-bearing property is not "layers can be lazy" but "asking whether a cell HAS
///         layers does not decode them". <c>HasAny</c> is evaluated per cell across an entire
///         worldspace by <c>WorldSpatialIndex</c>, <c>WorldMapViewportMath</c> and
///         <c>CellWorldspaceAuthorityApplier</c>; if that question materialized layers, the lazy
///         route would drag all ~40k cells through the decode gate and save nothing.
///     </para>
/// </summary>
public sealed class LandVisualDataLazyLayerTests
{
    private static LandTextureLayer Layer(uint formId)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Base,
            TextureFormId = formId,
            Quadrant = 0
        };
    }

    [Fact]
    public void Asking_whether_layers_exist_does_not_decode_them()
    {
        var calls = 0;
        var visual = new LandVisualData
        {
            TextureLayersProvider = () =>
            {
                calls++;
                return [Layer(0x123)];
            },
            HasLazyTextureLayers = true
        };

        Assert.True(visual.HasTextureLayers);
        Assert.True(visual.HasAny);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Reading_the_layers_decodes_them()
    {
        var calls = 0;
        var visual = new LandVisualData
        {
            TextureLayersProvider = () =>
            {
                calls++;
                return [Layer(0x123)];
            },
            HasLazyTextureLayers = true
        };

        var layers = visual.TextureLayers;

        Assert.Equal(1, calls);
        Assert.Equal(0x123u, Assert.Single(layers).TextureFormId);
    }

    [Fact]
    public void A_cell_with_no_layers_reports_none_without_a_provider_call()
    {
        // The injector attaches a provider only to cells its cheap texture-set probe says have
        // layers, so "no layers" must be answerable with no provider at all.
        var visual = new LandVisualData { VertexColors = new byte[33 * 33 * 3] };

        Assert.False(visual.HasTextureLayers);
        Assert.True(visual.HasAny);
        Assert.Empty(visual.TextureLayers);
    }

    [Fact]
    public void Explicitly_set_layers_win_over_a_provider()
    {
        var calls = 0;
        var visual = new LandVisualData
        {
            TextureLayers = [Layer(0xAAA)],
            TextureLayersProvider = () =>
            {
                calls++;
                return [Layer(0xBBB)];
            }
        };

        Assert.Equal(0xAAAu, Assert.Single(visual.TextureLayers).TextureFormId);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Collection_initializer_syntax_still_populates_the_layer_list()
    {
        // Regression pin. `TextureLayers = { … }` is NOT an assignment — it compiles to Add() calls
        // against whatever the GETTER returns. A first cut of the lazy route used a null backing
        // field, so every element added here vanished into a throwaway list and the merge helpers
        // silently lost their layers. Existing production code and tests use this syntax, so the
        // getter must keep handing back a stable, mutable list when no provider is attached.
        var visual = new LandVisualData
        {
            TextureLayers = { Layer(0x1), Layer(0x2) }
        };

        Assert.Equal(2, visual.TextureLayers.Count);
        Assert.True(visual.HasTextureLayers);
    }
}
