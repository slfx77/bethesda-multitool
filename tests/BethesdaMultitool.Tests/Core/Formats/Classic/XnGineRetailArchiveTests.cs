using System;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the XnGine BSA layer against the retail Daggerfall and Battlespire
///     installs (<c>RUN_BUCKET_B=1</c>). The synthetic suite pins the grammar; these pin what
///     only shipped data can — that every retail archive tiles, that the numbered form and the
///     per-entry compression are real, and that decompression yields recognizable content.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class XnGineRetailArchiveTests
{
    private static string RequireDaggerfallRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    private static string RequireBattlespireRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire (GAMEDATA)"));
        return root!;
    }

    [Fact]
    public void Daggerfall_AllFiveArchives_OpenWithTheirKnownCounts()
    {
        var root = RequireDaggerfallRoot();

        // Counts pinned deliberately: a change means the data changed, which is what this suite
        // should catch. ARCH3D is the numbered form; the rest are named.
        foreach (var (name, expectedCount, numbered) in new[]
                 {
                     ("MONSTER.BSA", 103, false),
                     ("MIDI.BSA", 131, false),
                     ("MAPS.BSA", 248, false),
                     ("BLOCKS.BSA", 1295, false),
                     ("ARCH3D.BSA", 10251, true)
                 })
        {
            var path = Path.Combine(root, name);
            Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(name));

            var archive = XnGineBsaParser.Parse(path);
            Assert.Equal(expectedCount, archive.Entries.Count);
            Assert.Equal(numbered, archive.IsNumbered);

            // Daggerfall ships nothing compressed; the flag word is how Battlespire differs.
            Assert.All(archive.Entries, e => Assert.False(e.Compressed));
        }
    }

    [Fact]
    public void Daggerfall_MapsArchive_CarriesTheLocationTables()
    {
        var root = RequireDaggerfallRoot();
        var path = Path.Combine(root, "MAPS.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("MAPS.BSA"));

        using var reader = ArchiveReader.Open(path);
        var names = reader.ListFiles().Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The per-region triplets the location layer will consume next.
        Assert.Contains("MAPTABLE.000", names);
        Assert.Contains("MAPPITEM.000", names);
        Assert.Contains("MAPDITEM.000", names);

        var table = reader.ReadFile("MAPTABLE.000");
        Assert.NotNull(table);
        Assert.True(table.Length > 0);
    }

    [Fact]
    public void Battlespire_EveryXnGineContainer_OpensAndTheOddOnesRefuse()
    {
        var root = RequireBattlespireRoot();

        foreach (var (name, expectedCount, numbered) in new[]
                 {
                     ("3D.BS6", 2115, false),
                     ("3D.BSA", 2400, false),
                     ("BS6.BSA", 47, false),
                     ("BSI.BSA", 2599, false),
                     ("FLC.BSA", 164, false),
                     ("TXT.BSA", 254, false),
                     ("SPIRE.SND", 370, true)
                 })
        {
            var path = Path.Combine(root, name);
            Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(name));

            var archive = XnGineBsaParser.Parse(path);
            Assert.Equal(expectedCount, archive.Entries.Count);
            Assert.Equal(numbered, archive.IsNumbered);
        }

        // DMKA/DMOG/DMZR.BS6 carry type word 0x4C52 — a different format that must not be claimed.
        foreach (var odd in new[] { "DMKA.BS6", "DMOG.BS6", "DMZR.BS6" })
        {
            var path = Path.Combine(root, odd);
            if (File.Exists(path))
            {
                Assert.False(XnGineBsaParser.TryProbe(path), $"{odd} is not an XnGine BSA and must refuse.");
            }
        }
    }

    [Fact]
    public void Battlespire_EveryCompressedMesh_DecompressesToAVersionedModel()
    {
        var root = RequireBattlespireRoot();
        var path = Path.Combine(root, "3D.BS6");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("3D.BS6"));

        using var reader = ArchiveReader.Open(path);
        var entries = reader.ListFiles();

        Assert.All(entries, e => Assert.True(e.Compressed, $"{e.Name} should be flagged compressed."));

        // Every mesh in the archive must decompress to a .3D model, which always opens with its
        // ASCII version tag. 2,115 independent streams all landing on "v2." is the proof the
        // byte-swapped code pair and the split window prefill are right — get either wrong and
        // this is garbage, not off-by-a-little.
        var decoded = 0;
        foreach (var entry in entries)
        {
            var bytes = reader.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            Assert.True(bytes.Length >= 4, $"{entry.Name} decompressed to only {bytes.Length} bytes.");
            Assert.Equal("v2."u8.ToArray(), bytes[..3]);
            decoded++;
        }

        Assert.Equal(2115, decoded);
    }
}
