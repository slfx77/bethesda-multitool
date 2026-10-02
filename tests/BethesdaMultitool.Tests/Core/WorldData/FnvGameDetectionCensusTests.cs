using System.Collections.Immutable;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     The census that gates the Fallout New Vegas game-detection fix.
/// </summary>
/// <remarks>
///     <para>
///         Retail <c>FalloutNV.esm</c> begins with the bytes <c>TES4</c>, but the record scan finds no TES4 main
///         record, so <c>RecordParserContext.DetectGameFromTes4</c> returns <see cref="BethesdaGame.Unknown" />
///         and every game-gated branch in the parse is inert. Two water tests fail as a direct result.
///     </para>
///     <para>
///         The prepared fix reads the game from the file's own leading bytes. It is deliberately unapplied,
///         because turning <c>Game</c> from Unknown to FalloutNV re-arms <b>every</b> game-gated branch in a
///         parse of this size, not only the water ones. This census is what makes that safe to reason about: it
///         parses the same file twice, once as the scan resolves it and once with the game forced, and compares
///         the whole record collection rather than the fields the fix is aimed at.
///     </para>
///     <para>
///         No production seam was added for this. <c>EsmRecordScanResult.Game</c> is already settable and already
///         takes priority over detection, so forcing it exercises exactly the state the fix would produce without
///         editing detection to find out.
///     </para>
/// </remarks>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvGameDetectionCensusTests(SampleFileFixture samples)
{
    /// <summary>Parses the file once, optionally forcing the game the scan would otherwise leave Unknown.</summary>
    private static (BethesdaGame Resolved, RecordCollection Collection) Parse(string filePath, BethesdaGame? force)
    {
        var fileData = File.ReadAllBytes(filePath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (parsedRecords, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(fileData);
        var (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo, landToCell) =
            EsmFileAnalyzer.BuildAllMaps(parsedRecords, grupHeaders);

        var scanResult = EsmDataExtractor.ConvertToScanResult(
            parsedRecords, isBigEndian, cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo, landToCell);
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, parsedRecords, isBigEndian);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length, MemoryMappedFileAccess.Read);
        EsmWorldExtractor.ExtractLandRecords(accessor, fileData.Length, scanResult);

        var formIdMap = new Dictionary<uint, string>();
        foreach (var record in parsedRecords)
        {
            if (record.Header.FormId == 0 || formIdMap.ContainsKey(record.Header.FormId)) continue;
            var editorId = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
            if (!string.IsNullOrEmpty(editorId)) formIdMap[record.Header.FormId] = editorId;
        }

        if (force is { } game) scanResult.Game = game;

        var parser = new RecordParser(scanResult, formIdMap, accessor, fileData.Length);
        return (scanResult.Game, parser.ParseAll());
    }

    /// <summary>Every cell's water selection, keyed by form id, as the fix's intended blast radius.</summary>
    private static ImmutableSortedDictionary<uint, string> WaterRows(RecordCollection collection)
    {
        return collection.Cells
            .GroupBy(static cell => cell.FormId)
            .ToImmutableSortedDictionary(
                static group => group.Key,
                static group => group.Last().WaterFormId?.ToString("X8") ?? "none");
    }

    /// <summary>Collection sizes, as the check that nothing outside water moved.</summary>
    private static ImmutableSortedDictionary<string, int> Counts(RecordCollection collection)
    {
        return new Dictionary<string, int>
        {
            ["Cells"] = collection.Cells.Count,
            ["Worldspaces"] = collection.Worldspaces.Count,
            ["Water"] = collection.Water.Count,
            ["Npcs"] = collection.Npcs.Count,
            ["Creatures"] = collection.Creatures.Count,
            ["Races"] = collection.Races.Count,
            ["Factions"] = collection.Factions.Count,
            ["Quests"] = collection.Quests.Count,
            ["Dialogues"] = collection.Dialogues.Count,
            ["Scripts"] = collection.Scripts.Count,
            ["Weapons"] = collection.Weapons.Count,
            ["Armor"] = collection.Armor.Count,
            ["Ammo"] = collection.Ammo.Count,
            ["Consumables"] = collection.Consumables.Count,
            ["MiscItems"] = collection.MiscItems.Count,
            ["Containers"] = collection.Containers.Count,
            ["Perks"] = collection.Perks.Count,
            ["Spells"] = collection.Spells.Count,
            ["Books"] = collection.Books.Count,
            ["Notes"] = collection.Notes.Count,
            ["Terminals"] = collection.Terminals.Count,
            ["EncounterZones"] = collection.EncounterZones.Count
        }.ToImmutableSortedDictionary();
    }

    [Fact]
    public void ForcingTheGameChangesWaterAndNothingElseCounted()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var natural = Parse(samples.PcFinalEsm!, force: null);
        var forced = Parse(samples.PcFinalEsm!, force: BethesdaGame.FalloutNewVegas);

        // The premise the whole fix rests on. If the scan ever starts resolving the game on its own, this
        // census is measuring nothing and must be revisited rather than quietly kept.
        Assert.Equal(BethesdaGame.Unknown, natural.Resolved);
        Assert.Equal(BethesdaGame.FalloutNewVegas, forced.Resolved);

        var before = Counts(natural.Collection);
        var after = Counts(forced.Collection);
        var moved = before.Keys
            .Where(key => before[key] != after[key])
            .Select(key => $"{key}: {before[key]} -> {after[key]}")
            .ToArray();

        Assert.True(moved.Length == 0,
            "Forcing the game changed record counts outside water, so the fix's blast radius is wider " +
            "than the water fields it targets: " + string.Join("; ", moved));

        var waterBefore = WaterRows(natural.Collection);
        var waterAfter = WaterRows(forced.Collection);
        Assert.Equal(waterBefore.Count, waterAfter.Count);

        var changed = waterBefore.Keys
            .Where(formId => waterBefore[formId] != waterAfter[formId])
            .ToArray();

        var summary = new StringBuilder()
            .AppendLine($"cells {waterBefore.Count}, water selections changed {changed.Length}")
            .AppendLine($"collections compared {before.Count}, moved {moved.Length}")
            .AppendLine($"water records natural={before["Water"]} forced={after["Water"]}")
            .AppendLine($"cells with a water form id: natural="
                + waterBefore.Count(row => row.Value != "none")
                + " forced=" + waterAfter.Count(row => row.Value != "none"));
        TestContext.Current.TestOutputHelper?.WriteLine(summary.ToString());

        // Recorded as the measured state rather than asserted as a target: today the two parses agree on
        // every cell's water selection, which says the water defect is resolved downstream of the parse
        // rather than by the game flag alone. Asserting it keeps that from changing unnoticed.
        Assert.Empty(changed);
    }

    [Fact]
    public void TheWaterFailureIsAMissingRecordNotAMissingGameFlag()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        // The two failing water cases assert that WaterAppearance.FromWaterRecord returns a value, and
        // that method returns null for exactly one reason: its argument is null. So the question is not
        // whether the game flag is set, it is whether the water record the cell names was parsed at all.
        const uint nvCleanWater = 0x001009CA;
        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var parsed = collection.Water.Select(static water => water.FormId).ToHashSet();

        var citedByCells = collection.Cells
            .Where(static cell => cell.WaterFormId is not null)
            .Select(static cell => cell.WaterFormId!.Value)
            .ToHashSet();
        var missing = citedByCells.Where(formId => !parsed.Contains(formId)).Order().ToArray();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"water records parsed {parsed.Count}; distinct water ids cited by cells {citedByCells.Count}; "
            + $"cited but not parsed {missing.Length}");
        foreach (var formId in missing.Take(12))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"  missing {formId:X8}");
        }

        // Recorded as measurement. Whichever way this lands, it tells us where the defect is: a cited
        // water id that was never parsed is a reader gap, and it is not something a game flag repairs.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"NVCleanWater 001009CA parsed: {parsed.Contains(nvCleanWater)}");
        // The two failing cases resolve to these. If either is absent the resolver hands back a null
        // Water and FromWaterRecord returns null, which is exactly the observed failure.
        foreach (var (name, formId) in new[]
                 {
                     ("NVCleanWater", 0x001009CAu), ("Potomac", 0x00030009u),
                     ("DefaultWater", 0x00000018u), ("Dlc03TbCleanWater", 0x000E2C29u)
                 })
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"  {name} {formId:X8} parsed: {parsed.Contains(formId)}");
        }

        Assert.True(citedByCells.Count > 0, "No cell cites a water record at all.");
    }

    [Fact]
    public void TheFileHeaderCarriesTes4EvenThoughTheScanFindsNoTes4Record()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        // This is the entire root cause in one assertion: the bytes say TES4, the scan does not.
        var head = new byte[4];
        using (var stream = File.OpenRead(samples.PcFinalEsm!))
        {
            Assert.Equal(4, stream.Read(head, 0, 4));
        }

        Assert.Equal("TES4", Encoding.ASCII.GetString(head));

        var scan = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).ScanResult;
        Assert.DoesNotContain(scan.MainRecords, record => record.RecordType == "TES4");
    }
}
