using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Scene;

public sealed class ReferenceEnabledOverrideTests
{
    [Fact]
    public void Retail_HooverFireMeshFollowsInitiallyDisabledXespParent_AndOverridesVisibility()
    {
        // Retail FalloutNV.esm fixture (HooverDamExtMid): the drawable MSTT FXFireMed01
        // REFR is XESP-slaved to VHDBattleEffectsMarker 0x0015D98C. The parent carries
        // main-record flags 0x00000C00, including Initially Disabled. The model is present
        // in Fallout - Meshes.bsa and contains NiTriStrips/NiTriStripsData.
        const uint parentFormId = 0x0015D98C;
        const uint effectFormId = 0x0015E4A5;
        var parent = new PlacedReference
        {
            FormId = parentFormId,
            BaseFormId = 0x0000003B,
            EditorId = "VHDBattleEffectsMarker",
            IsInitiallyDisabled = true
        };
        var effect = new PlacedReference
        {
            FormId = effectFormId,
            BaseFormId = 0x00020CE1,
            BaseEditorId = "FXFireMed01",
            ModelPath = @"Effects\Ambient\FXFireMed01.NIF",
            X = 74021.945f,
            Y = 30128.693f,
            Z = 4358.886f,
            EnableParentFormId = parentFormId,
            EnableParentFlags = 0
        };
        CellRecord[] cells =
        [
            new()
            {
                FormId = 0x000DDD21,
                EditorId = "HooverDamExtMid",
                GridX = 18,
                GridY = 7,
                PlacedObjects = [parent, effect]
            }
        ];

        var xespDisabledRefs = PlacedReferenceEnableStateResolver.ResolveXespDisabledRefs(cells);
        var store = new ReferenceEnabledOverrideStore();
        var renderable = RenderableReference.TryBuild(
            effect,
            PlacedObjectCategory.Effects,
            xespDisabled: xespDisabledRefs.Contains(effectFormId));

        Assert.Contains(effectFormId, xespDisabledRefs);
        Assert.NotNull(renderable);
        Assert.True(renderable.Value.IsInitiallyDisabled);
        Assert.Equal(effect.ModelPath, renderable.Value.ModelPath);
        Assert.False(store.IsVisible(
            effectFormId,
            xespDisabledRefs.Contains(effectFormId),
            false));

        store.Set(effectFormId, ReferenceEnabledOverride.On);

        Assert.True(store.IsVisible(
            effectFormId,
            xespDisabledRefs.Contains(effectFormId),
            false));

        store.Set(effectFormId, ReferenceEnabledOverride.Off);

        Assert.False(store.IsVisible(
            effectFormId,
            xespDisabledRefs.Contains(effectFormId),
            true));
    }

    [Fact]
    public void Authored_DefaultFollowsResolvedInitialStateAndGlobalDiagnostic()
    {
        var store = new ReferenceEnabledOverrideStore();

        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x10));
        Assert.True(store.IsVisible(0x10, false, false));
        Assert.False(store.IsVisible(0x10, true, false));
        Assert.True(store.IsVisible(0x10, true, true));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void On_OverridesAuthoredDisabledStateWithoutAffectingSiblingPlacement()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.On);

        Assert.True(store.IsVisible(0x10, true, false));
        Assert.False(store.IsVisible(0x11, true, false));
        Assert.Equal(ReferenceEnabledOverride.Authored, store.Get(0x11));
    }

    [Fact]
    public void Off_WinsEvenWhenGlobalShowDisabledDiagnosticIsOn()
    {
        var store = new ReferenceEnabledOverrideStore();
        store.Set(0x10, ReferenceEnabledOverride.Off);

        Assert.False(store.IsVisible(0x10, false, true));
        Assert.False(store.IsVisible(0x10, true, true));
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
        Assert.False(store.IsVisible(0x10, true, false));
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
