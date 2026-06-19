using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins down the LRU eviction invariant the per-cell bitmap cache in
///     <c>WorldMapControl._layerCellBitmaps</c> relies on. The cache used to be a plain
///     <see cref="Dictionary{TKey,TValue}" /> and used <c>Keys.First()</c> as the LRU head
///     selector. After a mass-eviction (e.g. zoom-in collapses cap from 18k → 256), the
///     dict's internal free list was packed with low-index slots; subsequent <c>Add</c>
///     calls reused the most-recently-freed slot at a high index, so <c>Keys.First()</c>
///     walked past every free slot and returned the newly-added entry. Every cell in the
///     viewport appeared in the cache for one frame, then was immediately re-evicted —
///     the disappearing-cells bug reproduced in the 2026-06-05 Map2DProfiler trace.
///     <para>
///         These tests assert the actual invariant the cache needs:
///         (1) <see cref="OrderedDictionary{TKey,TValue}.GetAt" /> at index 0 is the
///         genuinely oldest entry, even after mass-eviction;
///         (2) Remove-then-Add moves an entry to the tail (LRU MRU end), not the front.
///     </para>
/// </summary>
public sealed class LayerCellBitmapsLruTests
{
    [Fact]
    public void GetAtZero_AfterMassEviction_ReturnsGenuinelyOldestRemaining()
    {
        // Mirrors the trace-observed scenario: cache fills to 16k entries, then cap shrinks
        // and the mass-evict loop removes from GetAt(0) until Count <= cap. After the loop,
        // every remaining entry must still be in insertion order.
        var dict = new OrderedDictionary<int, string>();
        for (var i = 0; i < 16_000; i++) dict.Add(i, $"v{i}");

        const int targetCap = 256;
        while (dict.Count > targetCap)
        {
            dict.RemoveAt(0);
        }

        Assert.Equal(targetCap, dict.Count);

        // The 256 survivors are the LAST 256 inserts (15744..15999).
        Assert.Equal(15_744, dict.GetAt(0).Key);
        Assert.Equal(15_999, dict.GetAt(targetCap - 1).Key);

        // Now stream-insert NEW entries — the smoking-gun scenario. Each Add must NOT
        // become the new head.
        for (var i = 20_000; i < 20_010; i++)
        {
            dict.Add(i, $"v{i}");
            // GetAt(0) must remain the original oldest survivor — NEVER the just-added.
            Assert.NotEqual(i, dict.GetAt(0).Key);
        }

        // Head is still the same oldest survivor.
        Assert.Equal(15_744, dict.GetAt(0).Key);
    }

    [Fact]
    public void RemoveThenAdd_MovesEntryToTail_NotInPlace()
    {
        // The LRU-on-read touch in EnsureHeightmapBitmap removes each visible cell and
        // re-Adds it so it goes to the tail. The indexer setter on OrderedDictionary
        // REPLACES IN PLACE, so the cache must use Remove + Add explicitly.
        var dict = new OrderedDictionary<int, string>();
        for (var i = 0; i < 5; i++) dict.Add(i, $"v{i}");

        Assert.Equal(0, dict.GetAt(0).Key);
        Assert.Equal(4, dict.GetAt(4).Key);

        // Touch key 0 — must move to the tail.
        var v = dict[0];
        dict.Remove(0);
        dict.Add(0, v);

        Assert.Equal(1, dict.GetAt(0).Key);
        Assert.Equal(0, dict.GetAt(4).Key);
    }

    [Fact]
    public void IndexerSetter_OnExistingKey_DoesNotMoveEntry()
    {
        // Documents the OrderedDictionary indexer-setter footgun: setting an existing
        // key's value preserves the original position. Why the cache must NOT use
        // dict[key] = newValue to move-to-tail.
        var dict = new OrderedDictionary<int, string>();
        for (var i = 0; i < 5; i++) dict.Add(i, $"v{i}");

        dict[0] = "updated";

        Assert.Equal(0, dict.GetAt(0).Key);
        Assert.Equal("updated", dict.GetAt(0).Value);
    }

    [Fact]
    public void StreamInsertAfterMassEvict_NoSelfEviction()
    {
        // End-to-end reproduction of the bug fix: after a mass-evict, the trim triggered
        // by inserting cell `i` must never pick `i` itself as the eviction victim. Older
        // entries (including older new ones, once we've filled past cap with new keys) can
        // and should be evicted — that's correct LRU behavior. The bug was specifically
        // "the cell I just added is the cell that gets evicted," which the assertion
        // `Assert.NotEqual(i, evicted)` pins down.
        var dict = new OrderedDictionary<int, string>();
        const int cap = 256;
        for (var i = 0; i < 16_000; i++) dict.Add(i, $"old{i}");
        while (dict.Count > cap) dict.RemoveAt(0);

        for (var i = 50_000; i < 50_300; i++)
        {
            dict.Add(i, $"new{i}");
            while (dict.Count > cap)
            {
                var evicted = dict.GetAt(0).Key;
                Assert.NotEqual(i, evicted);
                dict.RemoveAt(0);
            }
        }

        Assert.Equal(cap, dict.Count);
    }
}