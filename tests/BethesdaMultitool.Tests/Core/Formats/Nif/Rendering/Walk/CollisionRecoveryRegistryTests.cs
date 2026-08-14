using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

public sealed class CollisionRecoveryRegistryTests
{
    [Fact]
    public void MissingOwner_RemainsWarmable()
    {
        var registry = new CollisionRecoveryRegistry();

        var result = registry.ResolveCacheMiss(@"meshes\architecture\never-seen.nif");

        Assert.False(result.IsResolved);
        Assert.Null(result.Mesh);
        Assert.True(result.ShouldOfferWarmup);
    }

    [Fact]
    public void FaultedOrNullDecode_KeepsBoundsFallbackButStopsWarmupChurn()
    {
        var registry = new CollisionRecoveryRegistry();
        registry.MarkTerminalUnavailable(
            @"meshes\architecture\missing.nif",
            @"meshes\architecture\missing.nif",
            hasPublishedCollisionEntry: false);

        var result = registry.ResolveCacheMiss(@"meshes\architecture\missing.nif");

        Assert.False(result.IsResolved);
        Assert.Null(result.Mesh);
        Assert.False(result.ShouldOfferWarmup);
    }

    [Fact]
    public void AuthoritativeNone_SurvivesForcedCollisionLruEvictionAsLightweightState()
    {
        const string emptyPath = @"meshes\plants\authored-none.nif";
        var registry = new CollisionRecoveryRegistry();
        var none = CollisionCacheEntry.ResolvedNone;
        registry.Publish(emptyPath, emptyPath, none);

        using var collision = NewOneEntryCollisionLru();
        collision.Set(emptyPath, none);
        collision.Set(@"meshes\architecture\replacement.nif", PositiveEntry(10f));
        Assert.False(collision.TryPeek(emptyPath, out _));

        var result = registry.ResolveCacheMiss(emptyPath);
        Assert.True(result.IsResolved);
        Assert.Null(result.Mesh);
        Assert.Equal(CollisionMeshSource.None, result.Source);
        Assert.False(result.ShouldOfferWarmup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositiveOrRenderEmptyPublisher_RecoversExactVariantAfterForcedEviction(
        bool renderEmpty)
    {
        const string path = @"meshes\architecture\shared.nif";
        const string variant = @"meshes\architecture\shared.nif#A17B";
        var registry = new CollisionRecoveryRegistry();
        var entry = renderEmpty ? AuthoredEntry(20f) : PositiveEntry(20f);
        registry.Publish(path, variant, entry);

        using var collision = NewOneEntryCollisionLru();
        collision.Set(path, entry);
        collision.Set(@"meshes\architecture\replacement.nif", PositiveEntry(30f));
        Assert.False(collision.TryPeek(path, out _));

        var miss = registry.ResolveCacheMiss(path);
        Assert.False(miss.IsResolved);
        Assert.True(miss.ShouldOfferWarmup);
        Assert.True(registry.TryGetOwner(path, out var owner));
        Assert.Equal(variant, owner);
    }

    [Fact]
    public void UploadFailureAfterCollisionPublication_RemainsRepublishable()
    {
        const string path = @"meshes\architecture\gpu-upload-failed.nif";
        var registry = new CollisionRecoveryRegistry();
        registry.Publish(path, path, PositiveEntry(40f));

        var result = registry.ResolveCacheMiss(path);

        Assert.False(result.IsResolved);
        Assert.True(result.ShouldOfferWarmup);
    }

    [Fact]
    public void FailedVariantCannotPoisonAnExistingPublishedOwner()
    {
        const string path = @"meshes\architecture\shared.nif";
        const string goodVariant = @"meshes\architecture\shared.nif#GOOD";
        const string failedVariant = @"meshes\architecture\shared.nif#BAD";
        var registry = new CollisionRecoveryRegistry();
        registry.Publish(path, goodVariant, PositiveEntry(50f));

        registry.MarkTerminalUnavailable(path, failedVariant, hasPublishedCollisionEntry: true);

        Assert.True(registry.TryGetOwner(path, out var owner));
        Assert.Equal(goodVariant, owner);
        Assert.True(registry.ResolveCacheMiss(path).ShouldOfferWarmup);
    }

    [Fact]
    public void FailedVariantCannotPoisonRepublishableOwnerAfterCollisionEviction()
    {
        const string path = @"meshes\architecture\shared.nif";
        const string goodVariant = @"meshes\architecture\shared.nif#GOOD";
        const string failedVariant = @"meshes\architecture\shared.nif#BAD";
        var registry = new CollisionRecoveryRegistry();
        registry.Publish(path, goodVariant, PositiveEntry(55f));

        registry.MarkTerminalUnavailable(path, failedVariant, hasPublishedCollisionEntry: false);

        Assert.True(registry.TryGetOwner(path, out var owner));
        Assert.Equal(goodVariant, owner);
        var miss = registry.ResolveCacheMiss(path);
        Assert.False(miss.IsResolved);
        Assert.True(miss.ShouldOfferWarmup);
    }

    [Fact]
    public void OwnerRemovalIsVariantExactAndBoundsRegistryLifetime()
    {
        const string path = @"meshes\architecture\shared.nif";
        const string first = @"meshes\architecture\shared.nif#FIRST";
        const string current = @"meshes\architecture\shared.nif#CURRENT";
        var registry = new CollisionRecoveryRegistry();
        registry.Publish(path, first, PositiveEntry(60f));
        registry.Publish(path, current, PositiveEntry(70f));

        registry.RemoveOwner(path, first);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetOwner(path, out var owner));
        Assert.Equal(current, owner);

        registry.RemoveOwner(path, current);
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGetOwner(path, out _));
    }

    private static LruCache<string, CollisionCacheEntry> NewOneEntryCollisionLru() =>
        new(
            "CollisionRecoveryRegistryTests",
            ResourceCategory.CpuCache,
            maxEntries: 1,
            sizeOf: static (_, entry) => entry.ByteSize,
            comparer: StringComparer.OrdinalIgnoreCase);

    private static CollisionCacheEntry PositiveEntry(float z) =>
        CollisionCacheEntry.Create(
            @"meshes\architecture\ordinary.nif",
            HavokCollisionProvenance.AbsentOrUnsupported,
            null,
            null,
            () => TriangleMesh(z));

    private static CollisionCacheEntry AuthoredEntry(float z) =>
        CollisionCacheEntry.Create(
            @"meshes\architecture\collision-only.nif",
            HavokCollisionProvenance.AuthoredMesh,
            [new Vector3(0f, 0f, z), new Vector3(1f, 0f, z), new Vector3(0f, 1f, z)],
            [0, 1, 2],
            static () => throw new InvalidOperationException("Authored collision must win."));

    private static CollisionMesh TriangleMesh(float z) =>
        new(
            [new Vector3(0f, 0f, z), new Vector3(1f, 0f, z), new Vector3(0f, 1f, z)],
            [0, 1, 2]);
}
