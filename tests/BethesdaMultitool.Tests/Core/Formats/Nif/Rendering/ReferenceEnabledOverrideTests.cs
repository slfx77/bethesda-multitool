using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class ReferenceEnabledOverrideTests
{
    [Fact]
    public void Authored_HooverBattleEffectFollowsInitiallyDisabledXespParent_AndOnRevealsIt()
    {
        // Retail FalloutNV.esm fixture (HooverDamExtMid): FXExplosionArtilleryHoover
        // REFR 0x0017A277 is XESP-slaved to VHDBattleEffectsMarker 0x0015D98C.
        // The parent carries main-record flags 0x00000C00, including Initially Disabled.
        const uint parentFormId = 0x0015D98C;
        const uint effectFormId = 0x0017A277;
        var parent = new PlacedReference
        {
            FormId = parentFormId,
            BaseFormId = 0x0000003B,
            EditorId = "VHDBattleEffectsMarker",
            IsInitiallyDisabled = true,
        };
        var effect = new PlacedReference
        {
            FormId = effectFormId,
            BaseFormId = 0x0017A294,
            BaseEditorId = "FXExplosionArtilleryHoover",
            EnableParentFormId = parentFormId,
            EnableParentFlags = 0,
        };
        CellRecord[] cells =
            [
                new CellRecord
                {
                    FormId = 0x000DDD21,
                    EditorId = "HooverDamExtMid",
                    GridX = 18,
                    GridY = 7,
                    PlacedObjects = [parent, effect],
                },
            ];

        var xespDisabledRefs = PlacedReferenceEnableStateResolver.ResolveXespDisabledRefs(cells);
        var store = new ReferenceEnabledOverrideStore();

        Assert.Contains(effectFormId, xespDisabledRefs);
        Assert.False(store.IsVisible(
            effectFormId,
            isAuthoredDisabled: xespDisabledRefs.Contains(effectFormId),
            showInitiallyDisabled: false));

        store.Set(effectFormId, ReferenceEnabledOverride.On);

        Assert.True(store.IsVisible(
            effectFormId,
            isAuthoredDisabled: xespDisabledRefs.Contains(effectFormId),
            showInitiallyDisabled: false));
    }

    [Fact]
    public void Authored_DefaultFollowsResolvedInitialStateAndGlobalDiagnostic()
    {
        var store = new ReferenceEnabledOverrideStore();

        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x10));
        Assert.True(store.IsVisible(0x10, isAuthoredDisabled: false, showInitiallyDisabled: false));
        Assert.False(store.IsVisible(0x10, isAuthoredDisabled: true, showInitiallyDisabled: false));
        Assert.True(store.IsVisible(0x10, isAuthoredDisabled: true, showInitiallyDisabled: true));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void On_OverridesAuthoredDisabledStateWithoutAffectingSiblingPlacement()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.On);

        Assert.True(store.IsVisible(0x10, isAuthoredDisabled: true, showInitiallyDisabled: false));
        Assert.False(store.IsVisible(0x11, isAuthoredDisabled: true, showInitiallyDisabled: false));
        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x11));
    }

    [Fact]
    public void Off_WinsEvenWhenGlobalShowDisabledDiagnosticIsOn()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.Off);

        Assert.False(store.IsVisible(0x10, isAuthoredDisabled: false, showInitiallyDisabled: true));
        Assert.False(store.IsVisible(0x10, isAuthoredDisabled: true, showInitiallyDisabled: true));
    }

    [Fact]
    public void Authored_ResetRemovesOneOverrideAndRestoresPolicy()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.On);
        store.Set(0x11, ReferenceEnabledOverride.Off);
        var versionBeforeReset = store.Version;

        store.Set(0x10, ReferenceEnabledOverride.Authored);

        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x10));
        Assert.False(store.IsVisible(0x10, isAuthoredDisabled: true, showInitiallyDisabled: false));
        Assert.Equal(ReferenceEnabledOverride.Off, store.Get(0x11));
        Assert.Equal(1, store.Count);
        Assert.True(store.Version > versionBeforeReset);
    }

    [Fact]
    public void Clear_ResetsEveryInstanceAndNoOpWritesDoNotChurnVersion()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.On);
        var versionAfterSet = store.Version;
        store.Set(0x10, ReferenceEnabledOverride.On);
        Assert.Equal(versionAfterSet, store.Version);

        store.Clear();
        var versionAfterClear = store.Version;

        Assert.Equal(0, store.Count);
        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x10));
        store.Clear();
        store.Set(0x10, ReferenceEnabledOverride.Authored);
        Assert.Equal(versionAfterClear, store.Version);
    }
}
