using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Terrain texture layers are the one visual category where "the nearer source wins" is wrong.
///     A runtime <c>TESObjectLAND</c> describes the layers that were <b>resident</b> at the moment of
///     the crash; a parsed DMP record or the master ESM describes the whole <b>authored</b> set.
///     <para>
///         Measured 2026-08-31, when runtime LAND recovery first began producing layers: 18 of 41
///         emitted LAND records changed and ATXT/VTXT fell 794 → 759, concentrated in quadrants
///         dropping from 6 layers to 5. Retail <c>FalloutNV.esm</c> carries six-layer quadrants in
///         2,529 of its 19,133, so that was the runtime view shadowing authored data rather than
///         correcting it.
///     </para>
/// </summary>
public sealed class LandTextureLayerPrecedenceTests
{
    [Fact]
    public void RuntimeLayers_DoNotDisplaceAuthoredMasterLayers()
    {
        // The shape that regressed: the dump side is primary and carries a runtime-captured subset,
        // the master fallback carries the full authored set.
        var runtime = Data(VisualDataSource.Runtime, 5);
        var master = Data(VisualDataSource.MasterEsm, 6);

        var merged = LandVisualData.MergeForEmission(runtime, null, master);

        Assert.NotNull(merged);
        Assert.Equal(6, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.MasterEsm, merged.TextureLayersSource);
    }

    [Fact]
    public void RuntimeLayers_DoNotDisplaceLayersParsedFromTheDumpsOwnEsm()
    {
        // Dmp is authored data too — the big-endian plugin living inside the dump — so it outranks a
        // runtime capture for the same reason the master does.
        var merged = LandVisualData.MergeCategories(Data(VisualDataSource.Runtime, 4), Data(VisualDataSource.Dmp, 6));

        Assert.Equal(6, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.Dmp, merged.TextureLayersSource);
    }

    [Fact]
    public void RuntimeLayers_AreStillUsedWhenNothingAuthoredHasAny()
    {
        // The demotion must not become a ban: on a DMP-only browse there is no authored set, and the
        // runtime capture is the only terrain layering that exists.
        var merged = LandVisualData.MergeCategories(Data(VisualDataSource.Runtime, 3), null);

        Assert.Equal(3, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.Runtime, merged.TextureLayersSource);
    }

    [Fact]
    public void AmongAuthoredSources_CandidateOrderStillDecides()
    {
        // Demoting runtime must not reorder anything else: between two authored sets the primary
        // still wins, which is what keeps the dump's own content ahead of the master.
        var merged = LandVisualData.MergeCategories(Data(VisualDataSource.Dmp, 2), Data(VisualDataSource.MasterEsm, 6));

        Assert.Equal(2, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.Dmp, merged.TextureLayersSource);
    }

    [Fact]
    public void AggregateOnlyMasterStamp_CountsAsAuthored()
    {
        // BTD injection and the Morrowind parser used to set only the aggregate Source; the merge's
        // `?? Source` fallback was dead code (the per-field stamps are non-nullable), so their
        // authored layers merged as None. The effective-source properties make the fallback real:
        // an aggregate-only MasterEsm instance must outrank a runtime capture.
        var runtime = Data(VisualDataSource.Runtime, 5);
        var master = Data(VisualDataSource.MasterEsm, 6, stampPerField: false);

        var merged = LandVisualData.MergeCategories(runtime, master);

        Assert.Equal(6, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.MasterEsm, merged.TextureLayersSource);
    }

    [Fact]
    public void AggregateOnlyRuntimeStamp_IsStillDemoted()
    {
        // The demotion must not be dodgeable by omitting the per-field stamp on a runtime capture:
        // pass 1 is an authored allowlist, so an effective-Runtime (or effective-None) candidate
        // cannot win it.
        var runtime = Data(VisualDataSource.Runtime, 5, stampPerField: false);
        var master = Data(VisualDataSource.MasterEsm, 3);

        var merged = LandVisualData.MergeCategories(runtime, master);

        Assert.Equal(3, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.MasterEsm, merged.TextureLayersSource);
    }

    [Fact]
    public void LargerRuntimeSet_StillLosesToAuthored()
    {
        // The interesting direction: even when the runtime capture carries MORE layers than the
        // authored set, authored wins — the count difference is residency, not authorship.
        var merged = LandVisualData.MergeCategories(Data(VisualDataSource.Runtime, 10), Data(VisualDataSource.Dmp, 2));

        Assert.Equal(2, merged!.TextureLayers.Count);
        Assert.Equal(VisualDataSource.Dmp, merged.TextureLayersSource);
    }

    [Fact]
    public void MergeCarriesTheWinnersUnattachedVtxtCounts()
    {
        // VtxtCount/VtxtByteCount include the unattached tally; before this the merge dropped both,
        // so any merged record under-reported its VTXT diagnostics.
        var primary = Data(VisualDataSource.Dmp, 2) with { UnattachedVtxtCount = 3, UnattachedVtxtByteCount = 24 };

        var merged = LandVisualData.MergeCategories(primary, Data(VisualDataSource.MasterEsm, 6));

        Assert.Equal(3, merged!.UnattachedVtxtCount);
        Assert.Equal(24, merged.UnattachedVtxtByteCount);
    }

    private static LandVisualData Data(VisualDataSource source, int layerCount, bool stampPerField = true)
    {
        var layers = new List<LandTextureLayer>();
        for (var i = 0; i < layerCount; i++)
        {
            layers.Add(new LandTextureLayer
            {
                Kind = LandTextureLayerKind.Alpha,
                TextureFormId = (uint)(0x000A0000 + i),
                Quadrant = 0
            });
        }

        return stampPerField
            ? new LandVisualData { TextureLayers = layers, TextureLayersSource = source }
            : new LandVisualData { TextureLayers = layers, Source = source };
    }
}
