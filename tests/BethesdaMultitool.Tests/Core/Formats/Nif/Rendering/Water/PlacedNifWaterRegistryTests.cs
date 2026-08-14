using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class PlacedNifWaterRegistryTests
{
    [Fact]
    public void HideAfterRegistrationRemovesWaterAndShowRestoresItInTheStablePublication()
    {
        var store = new ReferenceEnabledOverrideStore();
        var registry = new PlacedNifWaterRegistry();
        var owner = CreateOwner(0x101);
        Assert.True(registry.Register(owner, [CreateSurface(owner.FormId)]));

        var published = registry.GetPublished(CreateKey(store), store);
        Assert.Single(published);

        store.Set(owner.FormId, ReferenceEnabledOverride.Off);
        var hidden = registry.GetPublished(CreateKey(store), store);
        Assert.Same(published, hidden);
        Assert.Empty(hidden);

        store.Set(owner.FormId, ReferenceEnabledOverride.On);
        var shownAgain = registry.GetPublished(CreateKey(store), store);
        Assert.Same(published, shownAgain);
        Assert.Single(shownAgain);
        Assert.Equal(owner.FormId, shownAgain[0].WaterFormId);
    }

    [Fact]
    public void ExplicitStateOverridesOnlyAuthoredState()
    {
        var store = new ReferenceEnabledOverrideStore();
        var registry = new PlacedNifWaterRegistry();
        var owner = CreateOwner(0x202, initiallyDisabled: true);
        registry.Register(owner, [CreateSurface(owner.FormId)]);

        Assert.Empty(registry.GetPublished(CreateKey(store), store));
        Assert.Single(registry.GetPublished(CreateKey(store, showInitiallyDisabled: true), store));

        store.Set(owner.FormId, ReferenceEnabledOverride.Off);
        Assert.Empty(registry.GetPublished(CreateKey(store, showInitiallyDisabled: true), store));

        store.Set(owner.FormId, ReferenceEnabledOverride.On);
        Assert.Single(registry.GetPublished(CreateKey(store), store));
    }

    [Fact]
    public void GrassMarkerImposterAndCategoryGatesFilterTheirOwnersIndependently()
    {
        var store = new ReferenceEnabledOverrideStore();
        var registry = new PlacedNifWaterRegistry();
        Register(registry, CreateOwner(1, isGrass: true));
        Register(registry, CreateOwner(2, isMarker: true));
        Register(registry, CreateOwner(3, isImposter: true));
        Register(registry, CreateOwner(4, category: PlacedObjectCategory.Effects));
        Register(registry, CreateOwner(5));

        Assert.Equal(
            [1u, 2u, 3u, 4u, 5u],
            PublishedIds(registry, CreateKey(store, showMarkers: true, showImposters: true), store));
        Assert.Equal(
            [2u, 3u, 4u, 5u],
            PublishedIds(
                registry,
                CreateKey(store, showGrass: false, showMarkers: true, showImposters: true),
                store));
        Assert.Equal(
            [1u, 3u, 4u, 5u],
            PublishedIds(registry, CreateKey(store, showMarkers: false, showImposters: true), store));
        Assert.Equal(
            [1u, 2u, 4u, 5u],
            PublishedIds(registry, CreateKey(store, showMarkers: true, showImposters: false), store));
        Assert.Equal(
            [1u, 2u, 3u, 5u],
            PublishedIds(
                registry,
                CreateKey(
                    store,
                    hiddenCategories: [PlacedObjectCategory.Effects],
                    showMarkers: true,
                    showImposters: true),
                store));

        // Explicit On wins over authored state only; it must not punch through a layer gate.
        store.Set(1, ReferenceEnabledOverride.On);
        Assert.DoesNotContain(
            1u,
            PublishedIds(
                registry,
                CreateKey(store, showGrass: false, showMarkers: true, showImposters: true),
                store));
    }

    [Fact]
    public void CategoryMaskIsDeterministicAndSupportsEveryDefinedCategory()
    {
        var forward = Enum.GetValues<PlacedObjectCategory>();
        var reverse = forward.Reverse();

        Assert.All(forward, category => Assert.InRange((int)category, 0, 63));
        Assert.Equal(
            ReferenceVisibilityKey.BuildHiddenCategoryMask(forward),
            ReferenceVisibilityKey.BuildHiddenCategoryMask(reverse));
        Assert.Equal(
            ReferenceVisibilityKey.BuildHiddenCategoryMask(
                [PlacedObjectCategory.Effects, PlacedObjectCategory.Static, PlacedObjectCategory.Effects]),
            ReferenceVisibilityKey.BuildHiddenCategoryMask(
                [PlacedObjectCategory.Static, PlacedObjectCategory.Effects]));
    }

    [Fact]
    public void ClearDropsOwnershipAndAllowsTheSameFormIdToRegisterForTheNextScene()
    {
        var store = new ReferenceEnabledOverrideStore();
        var registry = new PlacedNifWaterRegistry();
        var owner = CreateOwner(0x303);
        Assert.True(registry.Register(owner, [CreateSurface(1)]));
        Assert.False(registry.Register(owner, [CreateSurface(2)]));

        registry.Clear();
        Assert.Empty(registry.GetPublished(CreateKey(store), store));
        Assert.True(registry.Register(owner, [CreateSurface(3)]));
        Assert.Equal(3u, Assert.Single(registry.GetPublished(CreateKey(store), store)).WaterFormId);
    }

    private static void Register(PlacedNifWaterRegistry registry, RenderableReference owner) =>
        Assert.True(registry.Register(owner, [CreateSurface(owner.FormId)]));

    private static uint[] PublishedIds(
        PlacedNifWaterRegistry registry,
        ReferenceVisibilityKey key,
        ReferenceEnabledOverrideStore store) =>
        registry.GetPublished(key, store).Select(surface => surface.WaterFormId).ToArray();

    private static ReferenceVisibilityKey CreateKey(
        ReferenceEnabledOverrideStore store,
        IEnumerable<PlacedObjectCategory>? hiddenCategories = null,
        bool showInitiallyDisabled = false,
        bool showGrass = true,
        bool showMarkers = false,
        bool showImposters = false) =>
        ReferenceVisibilityKey.Capture(
            store,
            hiddenCategories ?? [],
            showInitiallyDisabled,
            showGrass,
            showMarkers,
            showImposters);

    private static RenderableReference CreateOwner(
        uint formId,
        bool initiallyDisabled = false,
        bool isMarker = false,
        bool isImposter = false,
        PlacedObjectCategory category = PlacedObjectCategory.Static,
        bool isGrass = false) =>
        new(
            FormId: formId,
            WorldMatrix: Matrix4x4.Identity,
            ModelPath: $"water-{formId:X8}.nif",
            BoundsCenter: Vector3.Zero,
            BoundsRadius: 1f,
            MeshId: formId,
            IsInitiallyDisabled: initiallyDisabled,
            IsMarker: isMarker,
            IsImposter: isImposter,
            Category: category,
            IsGrass: isGrass);

    private static NifWaterGeometry CreateSurface(uint waterFormId)
    {
        Assert.True(NifWaterGeometry.TryCreate(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2],
            out var surface));
        return Assert.IsType<NifWaterGeometry>(surface).WithWaterFormId(waterFormId);
    }
}
