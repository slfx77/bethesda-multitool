using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearanceAnalyzedIndexReuseTests(ITestOutputHelper output)
{
    private const uint NpcFormId = 0x00123456;
    private const uint RaceFormId = 0x00000907;

    [Fact]
    public void AnalyzedRecordPath_DecodesOnlySelectedPayloadWithoutWholeFileRescan()
    {
        var race = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(race, RaceFormId);
        var coefficients = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(coefficients, 1.25f);
        BinaryPrimitives.WriteSingleLittleEndian(coefficients.AsSpan(4), -2.5f);
        var recordBytes = EsmTestRecordBuilder.BuildRecordWithSubrecordsLE(
            "NPC_",
            NpcFormId,
            ("EDID", Encoding.ASCII.GetBytes("IndexedNpc\0")),
            ("FULL", Encoding.ASCII.GetBytes("Indexed NPC\0")),
            ("RNAM", race),
            ("FGGS", coefficients));
        var record = new DetectedMainRecord(
            "NPC_",
            checked((uint)(recordBytes.Length - 24)),
            0,
            NpcFormId,
            0,
            false)
        {
            HeaderSize = 24
        };
        var timings = new List<NpcAppearanceIndexBuildTiming>();

        var accessor = new TrackingMemoryAccessor(recordBytes);
        var index = NpcAppearanceIndexBuilder.Build(
            accessor,
            recordBytes.LongLength,
            [record],
            bigEndian: false,
            game: BethesdaGame.FalloutNewVegas,
            timingSink: timings.Add);

        var npc = Assert.Contains(NpcFormId, index.Npcs);
        Assert.Equal(BethesdaGame.FalloutNewVegas, index.Game);
        Assert.Equal("IndexedNpc", npc.EditorId);
        Assert.Equal("Indexed NPC", npc.FullName);
        Assert.Equal(RaceFormId, npc.RaceFormId);
        Assert.Equal([1.25f, -2.5f], Assert.IsType<float[]>(npc.FaceGenSymmetric));
        var timing = Assert.Single(timings);
        Assert.Equal("appearance-record-decode", timing.Stage);
        Assert.Equal(1, timing.RecordsVisited);
        Assert.Equal(1, timing.RecordsDecoded);
        Assert.Equal((long)record.DataSize, timing.BytesRead);
        Assert.DoesNotContain(timings, stage => stage.Stage == "record-descriptor-rescan");
        var read = Assert.Single(accessor.Reads);
        Assert.Equal(24, read.Position);
        Assert.Equal(record.DataSize, (uint)read.Count);
    }

    [Fact]
    public void AnalyzedRecordPath_TruncatedDescriptorFailsClosed()
    {
        var recordBytes = EsmTestRecordBuilder.BuildRecordWithSubrecordsLE(
            "NPC_",
            NpcFormId,
            ("EDID", Encoding.ASCII.GetBytes("MustNotAppear\0")));
        var invalidRecord = new DetectedMainRecord(
            "NPC_",
            checked((uint)recordBytes.Length),
            0,
            NpcFormId,
            0,
            false)
        {
            HeaderSize = 24
        };

        var accessor = new TrackingMemoryAccessor(recordBytes);
        var index = NpcAppearanceIndexBuilder.Build(
            accessor,
            recordBytes.LongLength,
            [invalidRecord],
            bigEndian: false,
            game: BethesdaGame.FalloutNewVegas);

        Assert.Empty(index.Npcs);
        Assert.Equal(BethesdaGame.FalloutNewVegas, index.Game);
        Assert.Empty(accessor.Reads);
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_AnalyzedRecordPathMatchesLegacyIndexAndReportsReduction()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        var esm = File.ReadAllBytes(esmPath!);

        var descriptorTimer = Stopwatch.StartNew();
        var analyzed = EsmDescriptorScanner.Scan(esm).ScanResult;
        descriptorTimer.Stop();

        var legacyTimer = Stopwatch.StartNew();
        var legacy = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        legacyTimer.Stop();

        var mappedTimings = new List<NpcAppearanceIndexBuildTiming>();
        var mappedTimer = Stopwatch.StartNew();
        var mapped = NpcAppearanceIndexBuilder.Build(
            new ByteArrayMemoryAccessor(esm),
            esm.LongLength,
            analyzed.MainRecords,
            bigEndian: false,
            game: analyzed.Game,
            timingSink: mappedTimings.Add);
        mappedTimer.Stop();

        Assert.Equal(legacy.Game, mapped.Game);
        AssertSameKeys(legacy.Npcs, mapped.Npcs);
        AssertSameKeys(legacy.Creatures, mapped.Creatures);
        AssertSameKeys(legacy.Races, mapped.Races);
        AssertSameKeys(legacy.Hairs, mapped.Hairs);
        AssertSameKeys(legacy.Eyes, mapped.Eyes);
        AssertSameKeys(legacy.HeadParts, mapped.HeadParts);
        AssertSameKeys(legacy.Armors, mapped.Armors);
        AssertSameKeys(legacy.ArmorAddons, mapped.ArmorAddons);
        AssertSameKeys(legacy.Weapons, mapped.Weapons);
        AssertSameKeys(legacy.Packages, mapped.Packages);
        AssertSameKeys(legacy.Idles, mapped.Idles);
        AssertSameKeys(legacy.FormLists, mapped.FormLists);
        AssertSameKeys(legacy.LeveledItems, mapped.LeveledItems);
        AssertSameKeys(legacy.LeveledItemRecords, mapped.LeveledItemRecords);
        AssertSameKeys(legacy.LeveledNpcs, mapped.LeveledNpcs);
        AssertSameKeys(legacy.LeveledNpcRecords, mapped.LeveledNpcRecords);
        AssertSameKeys(legacy.CombatStyles, mapped.CombatStyles);

        var legacyReynald = Assert.Contains(0x000222A8u, legacy.Npcs);
        var mappedReynald = Assert.Contains(0x000222A8u, mapped.Npcs);
        Assert.Equal(legacyReynald.EditorId, mappedReynald.EditorId);
        Assert.Equal(legacyReynald.FullName, mappedReynald.FullName);
        Assert.Equal(legacyReynald.RaceFormId, mappedReynald.RaceFormId);
        Assert.Equal(legacyReynald.FaceGenSymmetric, mappedReynald.FaceGenSymmetric);
        Assert.Equal(legacyReynald.InventoryItems?.Count, mappedReynald.InventoryItems?.Count);

        var decode = Assert.Single(mappedTimings);
        Assert.Equal("appearance-record-decode", decode.Stage);
        Assert.True(decode.BytesRead < esm.LongLength,
            $"Expected selective record reads below the {esm.LongLength:N0}-byte master; got {decode.BytesRead:N0}.");
        Assert.DoesNotContain(mappedTimings, stage => stage.Stage == "record-descriptor-rescan");

        output.WriteLine(
            "Oblivion NPC index: retained descriptor scan (already paid by analysis)={0:N2} ms; " +
            "legacy second scan+decode={1:N2} ms; mapped selective decode={2:N2} ms; " +
            "visited={3:N0}; decoded={4:N0}; selectedBytes={5:N0}/{6:N0}.",
            descriptorTimer.Elapsed.TotalMilliseconds,
            legacyTimer.Elapsed.TotalMilliseconds,
            mappedTimer.Elapsed.TotalMilliseconds,
            decode.RecordsVisited,
            decode.RecordsDecoded,
            decode.BytesRead,
            esm.LongLength);
    }

    private static void AssertSameKeys<T>(
        IReadOnlyDictionary<uint, T> expected,
        IReadOnlyDictionary<uint, T> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
    }

    private sealed class TrackingMemoryAccessor(byte[] data) : IMemoryAccessor
    {
        private readonly ByteArrayMemoryAccessor _inner = new(data);

        public List<(long Position, int Count)> Reads { get; } = [];

        public int ReadArray(long position, byte[] array, int offset, int count)
        {
            Reads.Add((position, count));
            return _inner.ReadArray(position, array, offset, count);
        }
    }
}
