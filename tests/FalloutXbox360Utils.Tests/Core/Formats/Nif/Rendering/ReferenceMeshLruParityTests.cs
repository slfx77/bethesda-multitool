using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Resources;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the <see cref="LruCache{TKey,TValue}" /> configuration semantics that
///     <c>ReferenceMeshCache12</c> relies on after its hand-rolled LRUs migrated onto the shared
///     primitive. The cache itself is <c>net10.0-windows</c>-only (WINDOWS_GUI), so — like
///     <c>CellMeshLruCacheTests</c> before — these tests drive an identically-configured LruCache
///     with stand-in types instead of the real <c>Node</c>/<c>CachedNifMesh12</c>.
/// </summary>
public sealed class ReferenceMeshLruParityTests
{
    private static LruCache<string, FakeNode> CreateMeshLru(int capacity)
    {
        return new LruCache<string, FakeNode>("ReferenceMeshLruParity", ResourceCategory.GpuResident,
            capacity,
            onEvicted: static (_, node) => node.Mesh?.Dispose(),
            comparer: StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eviction_cascade_disposes_mesh_assigned_after_insertion()
    {
        // The production pattern: a Node is inserted with Mesh = null (decode pending) and the
        // mesh is assigned IN PLACE later, without a re-Set. The eviction hook must see the
        // post-insertion state — the cache stores the reference, not a snapshot.
        var cache = CreateMeshLru(2);
        var node = new FakeNode();
        cache.Set("meshes\\a.nif", node);

        var mesh = new FakeMesh();
        node.Mesh = mesh; // decode completed → upload → assigned in place

        cache.Set("meshes\\b.nif", new FakeNode());
        cache.Set("meshes\\c.nif", new FakeNode()); // evicts "a", the LRU

        Assert.True(mesh.Disposed);
        Assert.False(cache.ContainsKey("meshes\\a.nif"));
    }

    [Fact]
    public void Evicting_a_pending_node_with_null_mesh_is_a_safe_no_op()
    {
        // A node whose decode is still in flight (Mesh null) can be evicted; the cascade must
        // be a no-op, and a later re-insert of the same key must dispose only the new lineage.
        var cache = CreateMeshLru(1);
        cache.Set("meshes\\a.nif", new FakeNode()); // Mesh stays null
        cache.Set("meshes\\b.nif", new FakeNode()); // evicts pending "a" — must not throw

        var reinserted = new FakeNode();
        cache.Set("meshes\\a.nif", reinserted); // evicts "b"
        var mesh = new FakeMesh();
        reinserted.Mesh = mesh;

        cache.Set("meshes\\c.nif", new FakeNode()); // evicts re-inserted "a"
        Assert.True(mesh.Disposed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Keys_are_case_insensitive_like_normalized_model_paths()
    {
        var cache = CreateMeshLru(4);
        var node = new FakeNode();
        cache.Set("meshes\\architecture\\A.NIF", node);

        Assert.True(cache.TryGet("MESHES\\ARCHITECTURE\\a.nif", out var hit));
        Assert.Same(node, hit);
        Assert.True(cache.TryPeek("Meshes\\Architecture\\A.nif", out _));
    }

    [Fact]
    public void Decoded_cache_config_byte_budget_negatives_and_oversized_survivor()
    {
        // Mirrors the decoded CPU cache configuration: byte budget via sizeOf, 1-byte negative
        // entries, no onEvicted. Pins the deliberate delta vs the old hand-rolled loop: an entry
        // larger than the whole budget SURVIVES its own insert (the old code self-evicted it,
        // which at tiny diagnostic budgets was a decode→store→self-evict→re-decode livelock).
        var cache = new LruCache<string, FakeDecodedValue>(
            "ReferenceDecodedParity", ResourceCategory.CpuCache,
            maxBytes: 100,
            sizeOf: static (_, v) => v.ByteSize,
            comparer: StringComparer.OrdinalIgnoreCase);

        cache.Set("neg1", new FakeDecodedValue(true, 1));
        cache.Set("neg2", new FakeDecodedValue(true, 1));
        cache.Set("mesh1", new FakeDecodedValue(false, 60));
        Assert.Equal(62, cache.EstimatedBytes);

        cache.Set("mesh2", new FakeDecodedValue(false, 50)); // 112 > 100 → evicts LRU until ≤100
        Assert.False(cache.ContainsKey("neg1"));
        Assert.False(cache.ContainsKey("neg2"));
        Assert.False(cache.ContainsKey("mesh1")); // 60+50 still > 100, so mesh1 goes too
        Assert.Equal(50, cache.EstimatedBytes);

        cache.Set("huge", new FakeDecodedValue(false, 500)); // larger than the whole budget
        Assert.True(cache.ContainsKey("huge")); // survives alone, over budget
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("huge", out var value));
        Assert.Equal(500, value.ByteSize);
    }

    /// <summary>Stand-in for CachedNifMesh12: disposal is the observable eviction cascade.</summary>
    private sealed class FakeMesh
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>Stand-in for the cache's Node: a mutable holder whose Mesh is assigned AFTER insertion.</summary>
    private sealed class FakeNode
    {
        public FakeMesh? Mesh { get; set; }
    }

    private readonly record struct FakeDecodedValue(bool IsNegative, long ByteSize);
}