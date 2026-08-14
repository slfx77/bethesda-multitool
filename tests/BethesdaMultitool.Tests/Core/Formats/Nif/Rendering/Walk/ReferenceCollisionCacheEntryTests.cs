using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using Xunit.Sdk;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

public sealed class ReferenceCollisionCacheEntryTests
{
    [Theory]
    [InlineData("Plants")]
    [InlineData("Tree")]
    public void StoreThenResolve_VegetationWithoutAuthoredHavok_ReturnsResolvedNone(
        string categoryName)
    {
        var category = Enum.Parse<PlacedObjectCategory>(categoryName);
        var visual = TriangleMesh(z: 10f);
        var visualBuilds = 0;
        var entry = CollisionCacheEntry.Create(
            @"meshes\landscape\trees\WastelandShrub01.nif",
            HavokCollisionProvenance.AbsentOrUnsupported,
            null,
            null,
            () =>
            {
                visualBuilds++;
                return visual;
            });

        var vegetation = CollisionMeshResolution.From(
            entry.Resolve(@"landscape\trees\WastelandShrub01.nif", category));
        var ordinary = CollisionMeshResolution.From(
            entry.Resolve(@"landscape\trees\WastelandShrub01.nif", PlacedObjectCategory.Landscape));

        Assert.True(vegetation.IsResolved);
        Assert.Null(vegetation.Mesh);
        Assert.Equal(CollisionMeshSource.None, vegetation.Source);
        Assert.True(ordinary.IsResolved);
        Assert.Same(visual, ordinary.Mesh);
        Assert.Equal(CollisionMeshSource.VisualFallback, ordinary.Source);
        Assert.Equal(1, visualBuilds);
    }

    [Theory]
    [InlineData("Plants")]
    [InlineData("Tree")]
    public void StoreThenResolve_VegetationWithAuthoredHavok_ReturnsAuthoredMesh(
        string categoryName)
    {
        var category = Enum.Parse<PlacedObjectCategory>(categoryName);
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        int[] triangles = [0, 1, 2];
        var entry = CollisionCacheEntry.Create(
            @"meshes\plants\SolidCactus01.nif",
            HavokCollisionProvenance.AuthoredMesh,
            positions,
            triangles,
            () => throw new XunitException("Visual soup must not be retained when authored Havok wins."));

        var result = CollisionMeshResolution.From(
            entry.Resolve(@"plants\SolidCactus01.nif", category));

        Assert.True(result.IsResolved);
        Assert.NotNull(result.Mesh);
        Assert.Same(positions, result.Mesh.Positions);
        Assert.Same(triangles, result.Mesh.Triangles);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, result.Source);
    }

    [Fact]
    public void StoreThenResolve_VisualSoupHonorsEffectAndSpeedTreePolicyAtCacheBoundary()
    {
        var visualBuilds = 0;
        CollisionMesh BuildVisual()
        {
            visualBuilds++;
            return TriangleMesh(z: 20f);
        }

        var effectEntry = CollisionCacheEntry.Create(
            @"meshes\effects\smoke\card.nif", HavokCollisionProvenance.AbsentOrUnsupported,
            null, null, BuildVisual);
        var speedTreeEntry = CollisionCacheEntry.Create(
            @"trees\WastelandShrub01.spt", HavokCollisionProvenance.AbsentOrUnsupported,
            null, null, BuildVisual);
        var missingPathEntry = CollisionCacheEntry.Create(
            null, HavokCollisionProvenance.AbsentOrUnsupported, null, null, BuildVisual);

        var effectCategory = effectEntry.Resolve(
            @"architecture\effect-volume.nif", PlacedObjectCategory.Effects);
        var effectPath = effectEntry.Resolve(
            @"meshes\effects\smoke\card.nif", PlacedObjectCategory.Unknown);
        var speedTree = speedTreeEntry.Resolve(
            @"landscape\trees\WastelandShrub01.spt", PlacedObjectCategory.Unknown);
        var missingPath = missingPathEntry.Resolve(null, PlacedObjectCategory.Unknown);

        Assert.Equal(CollisionMeshSource.None, effectCategory.Source);
        Assert.Null(effectCategory.Mesh);
        Assert.Equal(CollisionMeshSource.None, effectPath.Source);
        Assert.Null(effectPath.Mesh);
        Assert.Equal(CollisionMeshSource.None, speedTree.Source);
        Assert.Null(speedTree.Mesh);
        Assert.Equal(CollisionMeshSource.None, missingPath.Source);
        Assert.Null(missingPath.Mesh);
        Assert.Equal(0, visualBuilds);
    }

    [Fact]
    public void StoreThenResolve_OrdinaryPathCanVaryForEffectCategoryWithoutDuplicatingSoup()
    {
        var visual = TriangleMesh(z: 25f);
        var visualBuilds = 0;
        var entry = CollisionCacheEntry.Create(
            @"meshes\architecture\shared-volume.nif",
            HavokCollisionProvenance.AbsentOrUnsupported, null, null,
            () =>
            {
                visualBuilds++;
                return visual;
            });

        var effect = entry.Resolve(
            @"meshes\architecture\shared-volume.nif", PlacedObjectCategory.Effects);
        var ordinary = entry.Resolve(
            @"meshes\architecture\shared-volume.nif", PlacedObjectCategory.Architecture);

        Assert.Equal(CollisionMeshSource.None, effect.Source);
        Assert.Null(effect.Mesh);
        Assert.Equal(CollisionMeshSource.VisualFallback, ordinary.Source);
        Assert.Same(visual, ordinary.Mesh);
        Assert.Equal(1, visualBuilds);
    }

    [Fact]
    public void CacheEntryByteSize_ChargesStoredGeometryAndANonzeroNegativeFloor()
    {
        var visual = TriangleMesh(z: 30f);
        var positive = CollisionCacheEntry.Create(
            @"meshes\architecture\wall01.nif", HavokCollisionProvenance.AbsentOrUnsupported,
            null, null, () => visual);
        var negative = CollisionCacheEntry.Create(
            null, HavokCollisionProvenance.AbsentOrUnsupported, null, null,
            () => throw new XunitException("A path-invariant negative must not build visual soup."));

        Assert.Equal(visual.ByteSize, positive.ByteSize);
        Assert.Equal(128L, negative.ByteSize);
    }

    [Fact]
    public void ReferenceMeshCache_StoresSourceSeparatedEntryAndResolvesUsingPlacementCategory()
    {
        // ReferenceMeshCache12 is WINDOWS_GUI-only, so the portable suite exercises the real
        // CollisionCacheEntry above and pins the two production wiring points textually.
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
        var store = SourceContract.Extract(
            source,
            "private void StoreCollisionMesh",
            "private static CollisionCacheEntry BuildCollisionCacheEntry");

        Assert.Contains("var result = entry.Resolve(normalizedPath, category);", source,
            StringComparison.Ordinal);
        Assert.Contains("var entry = BuildCollisionCacheEntry(normalizedPath, decoded);", source,
            StringComparison.Ordinal);
        Assert.Contains("return CollisionCacheEntry.Create(", source, StringComparison.Ordinal);
        Assert.Contains("decoded.CollisionProvenance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacedObjectCategory.", store, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredNoncollidable_SuppressesVisualSoupForOrdinaryPlacement()
    {
        var visualBuilds = 0;
        var entry = CollisionCacheEntry.Create(
            @"meshes\clutter\junk\NV\WhiteHorseNettle.nif",
            HavokCollisionProvenance.AuthoredNoncollidable,
            null,
            null,
            () =>
            {
                visualBuilds++;
                return TriangleMesh(z: 40f);
            });

        var result = CollisionMeshResolution.From(
            entry.Resolve(@"clutter\junk\NV\WhiteHorseNettle.nif", PlacedObjectCategory.Activator));

        Assert.True(result.IsResolved);
        Assert.Null(result.Mesh);
        Assert.Equal(CollisionMeshSource.None, result.Source);
        Assert.Equal(0, visualBuilds);
    }

    [Fact]
    public void CollisionOnlyDecode_IsRetainedAndPublishedBeforeRenderEmptyReturn()
    {
        // The decoder/cache are WINDOWS_GUI-only. Portable tests cover the real extractor, disk
        // payload, and CollisionCacheEntry; this pins the platform-specific glue between them.
        var decoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshDecoder12.cs");
        var cache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
        var upload = SourceContract.Extract(
            cache,
            "private CachedNifMesh12? UploadDecodedMesh",
            "private static Vector3[]? ExtractParticleCenters");

        Assert.Contains("preserveEmptyModel: true", decoder, StringComparison.Ordinal);
        Assert.Contains("result = \"collision-only\";", decoder, StringComparison.Ordinal);
        Assert.Contains("StoreCollisionMesh(cacheKey, node, decoded);", cache, StringComparison.Ordinal);
        Assert.Contains("_collisionRecovery.Publish(normalizedPath, cacheKey, entry);", cache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CollisionOnly", cache, StringComparison.Ordinal);
        Assert.True(
            upload.IndexOf("StoreCollisionMesh(cacheKey, node, decoded);", StringComparison.Ordinal) <
            upload.IndexOf("if (totalVertexCount == 0 || totalIndexCount == 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceMeshCache_RecoveryUsesExactOwnerAndDrainsCompletedTasksBeforeEarlyReturns()
    {
        // State transitions are covered behaviorally by CollisionRecoveryRegistryTests. This pins the
        // WINDOWS_GUI-only cache glue that cannot be compiled into the platform-neutral test target.
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
        var warm = SourceContract.Extract(
            source,
            "public CollisionMeshResolution GetOrWarmCollisionMesh",
            "private bool TryGetCollisionOwnerNode");
        var republish = SourceContract.Extract(
            source,
            "private bool TryRepublishCollisionFromDecodedCache",
            "private void QueueCollisionRecovery");
        var resolve = SourceContract.Extract(
            source,
            "private CachedNifMesh12? ResolveExisting",
            "private void DrainCompletedDecodeTask");
        var drain = SourceContract.Extract(
            source,
            "private void DrainCompletedDecodeTask",
            "private void MarkCollisionTerminalUnavailable");
        var start = SourceContract.Extract(
            source,
            "private void StartQueuedDecodes",
            "private Task<DecodedNifMesh12?> StartDecodeTask");

        Assert.Contains("TryGetCollisionOwnerNode(normalizedPath, out var ownerKey, out var ownerNode)", warm,
            StringComparison.Ordinal);
        Assert.Contains("TryRepublishCollisionFromDecodedCache(ownerKey, ownerNode)", warm,
            StringComparison.Ordinal);
        Assert.Contains("QueueCollisionRecovery(ownerKey, ownerNode, priority)", warm,
            StringComparison.Ordinal);
        Assert.Contains("StoreCollisionMesh(ownerKey, ownerNode, decoded.Mesh);", republish,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UploadDecodedMesh", republish, StringComparison.Ordinal);

        var drainAt = resolve.IndexOf("DrainCompletedDecodeTask(modelPath, node);", StringComparison.Ordinal);
        Assert.True(drainAt >= 0);
        Assert.True(drainAt < resolve.IndexOf("if (node.Mesh is not null)", StringComparison.Ordinal));
        Assert.True(drainAt < resolve.IndexOf("if (node.ResolvedNull)", StringComparison.Ordinal));
        Assert.Contains("node.DecodeTask = null;", drain, StringComparison.Ordinal);
        Assert.Contains("StoreCollisionMesh(cacheKey, node, decoded);", drain, StringComparison.Ordinal);
        Assert.Contains(
            "(!node.CollisionRecoveryRequested && (node.ResolvedNull || node.Mesh is not null))",
            start,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvedNone_IsAnAuthoritativeNegativeForEveryCategory()
    {
        foreach (var category in Enum.GetValues<PlacedObjectCategory>())
        {
            var result = CollisionCacheEntry.ResolvedNone.Resolve(
                @"meshes\architecture\missing.nif", category);

            Assert.Null(result.Mesh);
            Assert.Equal(CollisionMeshSource.None, result.Source);
        }
    }

    [Fact]
    public void Create_RejectsMalformedAuthoredCollisionPayloads()
    {
        static CollisionCacheEntry Create(Vector3[] positions, int[] triangles) =>
            CollisionCacheEntry.Create(
                @"meshes\architecture\bad-collision.nif",
                HavokCollisionProvenance.AuthoredMesh,
                positions,
                triangles,
                () => throw new XunitException("Malformed authored data must fail before fallback."));

        Assert.Throws<ArgumentException>(() => Create(
            [new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => Create(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 3]));
        Assert.Throws<ArgumentException>(() => Create(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2, 0]));
    }

    private static CollisionMesh TriangleMesh(float z)
    {
        return new CollisionMesh(
            [new Vector3(0f, 0f, z), new Vector3(1f, 0f, z), new Vector3(0f, 1f, z)],
            [0, 1, 2]);
    }
}
