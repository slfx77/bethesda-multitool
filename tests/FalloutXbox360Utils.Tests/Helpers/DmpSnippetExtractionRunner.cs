using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     One-time runner that extracts DMP snippets from all available dump files.
///     Run this test class once with real DMP files present to generate snippet files.
///     The generated snippets can then be used by fast tests via <see cref="DmpSnippetReader" />.
/// </summary>
public sealed class DmpSnippetExtractionRunner(SampleFileFixture samples)
{
    private static readonly string OutputDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    /// <summary>
    ///     Extract snippets from all available dump files.
    ///     Exercises all code paths used by slow DMP tests.
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ExtractAllSnippets()
    {
        var dumps = new (string? Path, string Name)[]
        {
            (samples.DebugDump, "debug_dump"),
            (samples.ReleaseDump, "release_dump"),
            (samples.ReleaseDumpXex4, "xex4_dump"),
            (samples.ReleaseDumpXex44, "xex44_dump"),
            (SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp"), "memdebug_dump")
        };

        var extracted = 0;
        foreach (var (path, name) in dumps)
        {
            if (path == null)
            {
                continue;
            }

            await DmpSnippetExtractor.ExtractAsync(path, name, OutputDir, ExerciseAllCodePaths);
            extracted++;
        }

        Assert.True(extracted > 0, "No DMP files available for extraction");
    }

    /// <summary>
    ///     Exercises all code paths that the slow DMP tests use, ensuring the
    ///     <see cref="RecordingMemoryAccessor" /> captures every byte range needed.
    /// </summary>
    private static async Task ExerciseAllCodePaths(
        AnalysisResult analysisResult, IMemoryAccessor accessor, long fileSize)
    {
        var scanResult = analysisResult.EsmRecords!;
        var minidumpInfo = analysisResult.MinidumpInfo!;
        var allEntries = scanResult.RuntimeEditorIds;

        // --- Phase 1: RuntimeStructReader code paths ---

        var refrEntries = allEntries.Where(e => e.FormType is >= 0x3A and <= 0x3C).ToList();
        var npcEntries = allEntries.Where(e => e.FormType == 0x2A).ToList();
        var worldEntries = allEntries.Where(e => e.FormType == 0x41).ToList();
        var cellEntries = allEntries.Where(e => e.FormType == 0x39).ToList();

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor, fileSize, minidumpInfo,
            refrEntries, npcEntries, worldEntries, cellEntries,
            allEntries: allEntries);

        // NPC reads (RuntimeNpcDumpRegressionTests)
        foreach (var entry in npcEntries.Take(50))
        {
            reader.ReadRuntimeNpc(entry);
        }

        // World/Cell reads (RuntimeWorldCellDumpRegressionTests)
        reader.ReadAllWorldspaceCellMaps(worldEntries);
        foreach (var entry in cellEntries)
        {
            reader.ReadRuntimeCell(entry);
        }

        foreach (var entry in worldEntries)
        {
            reader.ReadRuntimeWorldspace(entry);
        }

        // --- Phase 1b: Non-probed reader for baseline tests that don't use allEntries ---
        var defaultReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor, fileSize, minidumpInfo,
            scanResult.RuntimeRefrFormEntries, null, worldEntries, cellEntries);

        defaultReader.ReadAllWorldspaceCellMaps(worldEntries);
        foreach (var entry in cellEntries)
        {
            defaultReader.ReadRuntimeCell(entry);
        }

        // NPC reads with non-probed reader (RuntimeNpcDumpRegressionTests uses RuntimeRefrFormEntries + npcEntries)
        var npcReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor, fileSize, minidumpInfo,
            scanResult.RuntimeRefrFormEntries, npcEntries);
        foreach (var entry in npcEntries.Take(50))
        {
            npcReader.ReadRuntimeNpc(entry);
        }

        // Specific named NPCs used by regression tests (may be beyond Take(50))
        foreach (var entry in npcEntries.Where(e =>
                     string.Equals(e.EditorId, "FortVulpesInculta", StringComparison.OrdinalIgnoreCase)))
        {
            reader.ReadRuntimeNpc(entry);
            npcReader.ReadRuntimeNpc(entry);
        }

        // Dialogue reads with non-probed reader (FormUsageIndexDumpIntegrationTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x46 && e.TesFormOffset.HasValue).Take(100))
        {
            npcReader.ReadRuntimeDialogueInfo(entry);
        }

        // REFR extra data census with default offsets (RuntimeRefrExtraDataBaselineTests)
        defaultReader.BuildRuntimeRefrExtraDataCensus(scanResult.RuntimeRefrFormEntries, 2000);

        // Per-type reads with default offsets (RuntimeProbeConsistencyTests compares probed vs default)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x0C && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimeRace(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x33 && e.TesFormOffset.HasValue).Take(100))
        {
            defaultReader.ReadProjectilePhysics(entry.TesFormOffset!.Value, entry.FormId);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x10 && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimeBaseEffect(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x14 && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimeSpell(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x13 && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimeEnchantment(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x56 && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimePerk(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x19 && e.TesFormOffset.HasValue).Take(50))
        {
            defaultReader.ReadRuntimeBook(entry);
        }

        // --- Phase 2: REFR reads + extra data census (probed reader) ---
        reader.BuildRuntimeRefrExtraDataCensus(scanResult.RuntimeRefrFormEntries, 2000);

        foreach (var entry in scanResult.RuntimeRefrFormEntries.Take(500))
        {
            reader.ReadRuntimeRefr(entry);
        }

        // Probe reads (RuntimeProbeConsistencyTests)
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        RuntimeRaceProbe.Probe(context, allEntries);
        RuntimeEffectProbe.Probe(context, allEntries);
        RuntimeMagicProbe.Probe(context, allEntries);
        RuntimeBookProbe.Probe(context, allEntries);

        // Per-type reads (RuntimeProbeConsistencyTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x0C && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeRace(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x10 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeBaseEffect(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x14 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeSpell(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x13 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeEnchantment(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x56 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimePerk(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x19 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeBook(entry);
        }

        // Projectile reads (RuntimeParityDumpIntegrationTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x33 && e.TesFormOffset.HasValue).Take(100))
        {
            reader.ReadProjectilePhysics(entry.TesFormOffset!.Value, entry.FormId);
        }

        // Dialogue reads (FormUsageIndexDumpIntegrationTests, RuntimeParityDumpIntegrationTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x46 && e.TesFormOffset.HasValue).Take(100))
        {
            reader.ReadRuntimeDialogueInfo(entry);
        }

        var topicEntries = allEntries.Where(e => e.FormType == 0x45 && e.TesFormOffset.HasValue).Take(100).ToList();
        foreach (var entry in topicEntries)
        {
            reader.ReadRuntimeDialogTopic(entry);
            var links = reader.WalkTopicQuestInfoList(entry);
            foreach (var link in links)
            {
                foreach (var infoEntry in link.InfoEntries.Take(20))
                {
                    reader.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
                }
            }
        }

        // Quest reads (RuntimeParityDumpIntegrationTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x47 && e.TesFormOffset.HasValue).Take(100))
        {
            reader.ReadRuntimeQuest(entry);
        }

        // World object reads (RuntimeParityDumpIntegrationTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x55 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeFormList(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType is 0x2C or 0x2D or 0x34 && e.TesFormOffset.HasValue)
                     .Take(50))
        {
            reader.ReadRuntimeLeveledList(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x15 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeActivator(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x1C && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeDoor(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x1E && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeLight(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x20 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeStatic(entry);
        }

        foreach (var entry in allEntries.Where(e => e.FormType == 0x27 && e.TesFormOffset.HasValue).Take(50))
        {
            reader.ReadRuntimeFurniture(entry);
        }

        // Package reads (PackageComparisonTests)
        foreach (var entry in allEntries.Where(e => e.FormType == 0x49 && e.TesFormOffset.HasValue).Take(100))
        {
            reader.ReadRuntimePackage(entry);
        }

        // Generic record reads (covers remaining FormTypes via PDB layouts)
        foreach (var entry in allEntries.Where(e => e.TesFormOffset.HasValue && !PdbStructLayouts.HasSpecializedReader(e.FormType)).Take(200))
        {
            reader.ReadGenericRecord(entry);
        }

        await Task.CompletedTask;
    }
}
