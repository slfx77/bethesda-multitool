using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>
///     The ported RenderWare reader run against the real Oblivion PSP data, end to end: pack →
///     resource wrapper → RenderWare stream → BINMESH. Opt-in: set <c>RUN_BUCKET_B=1</c>.
///     <para>
///         This is the test that makes the port worth having. The synthetic vectors prove the reader
///         does what the layout says; this proves the layout is what the game shipped, over
///         thousands of chunks nobody has parsed before. The load-bearing assertion is that
///         <b>every</b> BINMESH chunk parses AND its redundant <c>totalIndices</c> agrees with the
///         summed per-mesh counts — a redundant field agreeing thousands of times is evidence, a
///         chunk merely not throwing is not.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwBinMeshRetailTests
{
    private static string[] RequirePacks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.OblivionPspBuildsRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Oblivion PSP (cancelled betas)"));

        var packs = Directory
            .EnumerateFiles(root!, "GR.ARC", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.SkipWhen(packs.Length == 0, "No GR.ARC packs are staged.");
        return packs;
    }

    /// <summary>Every BINMESH body in one pack, paired with the root chunk it hangs under.</summary>
    private static List<(uint Root, byte[] Body)> BinMeshBodies(string pack)
    {
        var bytes = File.ReadAllBytes(pack);
        var archive = OblivionPspArchive.Parse(pack);
        var found = new List<(uint, byte[])>();

        foreach (var entry in archive.Entries)
        {
            if (entry.Size <= 0 || entry.Offset + entry.Size > bytes.Length)
            {
                continue;
            }

            var start = (int)entry.Offset;
            var end = start + (int)entry.Size;
            foreach (var resource in OblivionPspResourceReader.ReadResources(bytes.AsSpan(start, (int)entry.Size)))
            {
                if (!resource.IsRenderWareStream)
                {
                    continue;
                }

                var payload = start + resource.PayloadOffset;
                if (!RwChunk.TryRead(bytes, payload, end, out var root))
                {
                    continue;
                }

                Collect(bytes, root.PayloadOffset, root.End, root.Type, found, 0);
            }
        }

        return found;
    }

    private static void Collect(
        byte[] data, int offset, int end, uint root, List<(uint, byte[])> found, int depth)
    {
        if (depth > 12)
        {
            return;
        }

        foreach (var chunk in RwChunk.Siblings(data, offset, end))
        {
            if (chunk.Type == RwChunk.BinMeshPlugin)
            {
                found.Add((root, data[chunk.PayloadOffset..chunk.End]));
            }
            else if (RwChunk.IsContainer(chunk.Type))
            {
                Collect(data, chunk.PayloadOffset, chunk.End, root, found, depth + 1);
            }
        }
    }

    /// <summary>
    ///     ⚑ Every BINMESH chunk in the shipped data parses, and every one's redundant total agrees.
    /// </summary>
    [Fact]
    public void EveryRetailBinMeshParsesAndItsRedundantTotalAgrees()
    {
        var packs = RequirePacks();

        var total = 0;
        var parsed = 0;
        var agreed = 0;
        var empty = 0;
        var byRoot = new Dictionary<uint, int>();

        foreach (var pack in packs)
        {
            foreach (var (root, body) in BinMeshBodies(pack))
            {
                total++;
                byRoot[root] = byRoot.GetValueOrDefault(root) + 1;

                var mesh = RwBinMesh.TryParse(body);
                if (mesh is null)
                {
                    continue;
                }

                parsed++;
                if (mesh.TotalAgrees)
                {
                    agreed++;
                }

                if (mesh.Splits.Count == 0)
                {
                    empty++;
                }
            }
        }

        Assert.True(total > 8_000, $"Only {total} BINMESH chunks were reached; the join or walk regressed.");
        Assert.Equal(total, parsed);
        Assert.Equal(total, agreed);

        // The empty form is a large minority, not an anomaly — pinned so a future zero-guard fails
        // loudly instead of quietly reporting a partial match.
        Assert.True(empty > total / 5, $"Only {empty} of {total} are the empty form; expected a large minority.");

        // They hang under exactly the two geometry roots.
        Assert.Equal(
            new[] { RwChunk.World, RwChunk.Clump }.OrderBy(t => t),
            byRoot.Keys.OrderBy(t => t));
    }

    /// <summary>
    ///     Indices are u32. Read as u16 the layout does not tile even once, so the width is settled
    ///     by the data rather than inherited from the PC build — which is exactly the kind of
    ///     assumption a straight port would have carried in silently.
    /// </summary>
    [Fact]
    public void IndicesAreThirtyTwoBitAndAddressPlausibleVertices()
    {
        var packs = RequirePacks();

        var checkedSplits = 0;
        uint highest = 0;
        foreach (var (_, body) in BinMeshBodies(packs[^1]))
        {
            var mesh = RwBinMesh.TryParse(body);
            if (mesh is null)
            {
                continue;
            }

            foreach (var split in mesh.Splits)
            {
                checkedSplits++;
                foreach (var index in split.Indices)
                {
                    highest = Math.Max(highest, index);
                }
            }
        }

        Assert.True(checkedSplits > 0, "No splits were examined.");

        // A u16 misread would produce indices scattered across the whole 32-bit range; real vertex
        // indices stay small. This is what a wrong index width would actually look like.
        Assert.True(highest < 1 << 20, $"Highest index {highest} is implausible for vertex data.");
    }

    /// <summary>
    ///     The library id every resolved payload carries unpacks to a RenderWare 3.x version. Pinned
    ///     because the upstream code this was ported from compares the raw word instead, and would
    ///     keep working here by luck.
    /// </summary>
    [Fact]
    public void TheLibraryIdUnpacksToARenderWareThreeVersion()
    {
        var packs = RequirePacks();

        var bytes = File.ReadAllBytes(packs[^1]);
        var archive = OblivionPspArchive.Parse(packs[^1]);
        var versions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            if (entry.Size <= 0 || entry.Offset + entry.Size > bytes.Length)
            {
                continue;
            }

            foreach (var resource in OblivionPspResourceReader.ReadResources(
                         bytes.AsSpan((int)entry.Offset, (int)entry.Size)))
            {
                if (resource.IsRenderWareStream)
                {
                    versions.Add(RwLibraryVersion.Describe(resource.LibraryId));
                }
            }
        }

        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.StartsWith("3.", v, StringComparison.Ordinal));
    }
}
