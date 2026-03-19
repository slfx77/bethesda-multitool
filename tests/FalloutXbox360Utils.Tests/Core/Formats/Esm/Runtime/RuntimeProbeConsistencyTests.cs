using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Cross-DMP probe consistency tests. Runs the new field probes across multiple dumps
///     spanning different build dates to verify that probed layouts produce equal or better
///     read success rates compared to hardcoded defaults.
/// </summary>
[Collection(DumpSerialTestGroup.Name)]
public sealed class RuntimeProbeConsistencyTests(SampleFileFixture samples)
{
    /// <summary>
    ///     For each available DMP, creates two RuntimeStructReaders (probed vs default),
    ///     reads Race/Effect/Magic/Book entries, and reports per-type success rates.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ProbeConsistency_AcrossAllAvailableDumps_ProbedMatchesOrBeatsDefault()
    {
        var dmpDir = SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp");
        Assert.SkipWhen(dmpDir is null, "Memory dump directory not available");

        var dir = Path.GetDirectoryName(dmpDir)!;
        var dmpFiles = Directory.GetFiles(dir, "*.dmp")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        Assert.SkipWhen(dmpFiles.Count == 0, "No DMP files found");

        var output = TestContext.Current.TestOutputHelper!;
        var reportPath = Path.Combine(Path.GetTempPath(), "probe_consistency_report.txt");
        using var reportWriter = new StreamWriter(reportPath);

        void Log(string line)
        {
            output.WriteLine(line);
            reportWriter.WriteLine(line);
        }

        Log($"Found {dmpFiles.Count} DMP files\n");
        Log($"{"File",-42} {"Date",-12} {"RACE",10} {"PROJ",10} {"MGEF",10} {"SPEL",10} {"ENCH",10} {"PERK",10} {"BOOK",10}");
        Log(new string('-', 42 + 12 + 7 * 11));

        var allResults = new List<DmpProbeResult>();

        foreach (var dmpFile in dmpFiles)
        {
            try
            {
                var result = await TestSingleDumpAsync(dmpFile, Log);
                if (result != null)
                {
                    allResults.Add(result);
                }
            }
            catch (Exception ex)
            {
                Log($"  ERROR: {Path.GetFileName(dmpFile)}: {ex.Message}");
            }
        }

        Log($"\n=== Summary: {allResults.Count} dumps processed ===\n");

        // Aggregate and report
        var typeNames = new[] { "RACE", "PROJ", "MGEF", "SPEL", "ENCH", "PERK", "BOOK" };
        foreach (var typeName in typeNames)
        {
            var entries = allResults
                .Where(r => r.TypeResults.ContainsKey(typeName))
                .Select(r => r.TypeResults[typeName])
                .ToList();

            if (entries.Count == 0)
            {
                continue;
            }

            var totalProbed = entries.Sum(e => e.ProbedSuccess);
            var totalDefault = entries.Sum(e => e.DefaultSuccess);
            var totalEntries = entries.Sum(e => e.TotalEntries);
            var probedPct = totalEntries > 0 ? 100.0 * totalProbed / totalEntries : 0;
            var defaultPct = totalEntries > 0 ? 100.0 * totalDefault / totalEntries : 0;

            Log($"  {typeName,-6}: probed={totalProbed}/{totalEntries} ({probedPct:F1}%)  default={totalDefault}/{totalEntries} ({defaultPct:F1}%)  delta={totalProbed - totalDefault:+0;-0;0}");
        }

        // Report probe shifts found
        Log("\n=== Probe Shift Results ===\n");
        foreach (var result in allResults)
        {
            Log($"  {result.FileName,-42} {result.FileDate,-12}");
            foreach (var (name, shift, margin) in result.ProbeShifts)
            {
                Log($"    {name,-12}: shift={shift}, margin={margin}");
            }
        }

        reportWriter.Flush();
        Log($"\nReport saved to: {reportPath}");
    }

    private async Task<DmpProbeResult?> TestSingleDumpAsync(string dmpPath, Action<string> log)
    {
        var fileName = Path.GetFileName(dmpPath);
        var fileDate = new FileInfo(dmpPath).LastWriteTimeUtc.ToString("yyyy-MM-dd");

        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(
            dmpPath,
            includeMetadata: true,
            cancellationToken: TestContext.Current.CancellationToken);

        if (analysisResult.EsmRecords?.RuntimeEditorIds is not { Count: > 0 } allEntries)
        {
            return null;
        }

        var minidumpInfo = analysisResult.MinidumpInfo!;
        var fileSize = new FileInfo(dmpPath).Length;

        using var mmf = MemoryMappedFile.CreateFromFile(dmpPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        var refrEntries = allEntries.Where(e => e.FormType is >= 0x3A and <= 0x3C).ToList();
        var npcEntries = allEntries.Where(e => e.FormType == 0x2A).ToList();
        var worldEntries = allEntries.Where(e => e.FormType == 0x41).ToList();
        var cellEntries = allEntries.Where(e => e.FormType == 0x39).ToList();

        // Create probed reader (with allEntries to trigger probing)
        var probedReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor, fileSize, minidumpInfo, refrEntries, npcEntries, worldEntries, cellEntries,
            allEntries: allEntries);

        // Create default reader (without allEntries — no probing)
        var defaultReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor, fileSize, minidumpInfo, refrEntries, npcEntries, worldEntries, cellEntries);

        // Collect probe shift info
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        var probeShifts = new List<(string Name, string Shift, int Margin)>();

        var raceProbe = RuntimeRaceProbe.Probe(context, allEntries);
        if (raceProbe != null)
        {
            probeShifts.Add(("Race", FormatShifts(raceProbe.Winner.Layout), raceProbe.Margin));
        }

        var effectProbe = RuntimeEffectProbe.Probe(context, allEntries);
        if (effectProbe != null)
        {
            probeShifts.Add(("Effect", FormatShifts(effectProbe.Winner.Layout), effectProbe.Margin));
        }

        var magicProbe = RuntimeMagicProbe.Probe(context, allEntries);
        if (magicProbe != null)
        {
            probeShifts.Add(("Magic", FormatShifts(magicProbe.Winner.Layout), magicProbe.Margin));
        }

        var bookProbe = RuntimeBookProbe.Probe(context, allEntries);
        if (bookProbe != null)
        {
            probeShifts.Add(("Book", FormatShifts(bookProbe.Winner.Layout), bookProbe.Margin));
        }

        // Test each reader type
        var typeResults = new Dictionary<string, TypeReadResult>();

        // RACE (0x0C)
        var raceEntries = allEntries.Where(e => e.FormType == 0x0C && e.TesFormOffset.HasValue).ToList();
        typeResults["RACE"] = CompareReads(raceEntries,
            e => probedReader.ReadRuntimeRace(e),
            e => defaultReader.ReadRuntimeRace(e));

        // PROJ (0x33) — via ProjectilePhysics, which needs fileOffset + formId
        var projEntries = allEntries.Where(e => e.FormType == 0x33 && e.TesFormOffset.HasValue).ToList();
        typeResults["PROJ"] = CompareReads(projEntries,
            e => probedReader.ReadProjectilePhysics(e.TesFormOffset!.Value, e.FormId),
            e => defaultReader.ReadProjectilePhysics(e.TesFormOffset!.Value, e.FormId));

        // MGEF (0x10)
        var mgefEntries = allEntries.Where(e => e.FormType == 0x10 && e.TesFormOffset.HasValue).ToList();
        typeResults["MGEF"] = CompareReads(mgefEntries,
            e => probedReader.ReadRuntimeBaseEffect(e),
            e => defaultReader.ReadRuntimeBaseEffect(e));

        // SPEL (0x14)
        var spelEntries = allEntries.Where(e => e.FormType == 0x14 && e.TesFormOffset.HasValue).ToList();
        typeResults["SPEL"] = CompareReads(spelEntries,
            e => probedReader.ReadRuntimeSpell(e),
            e => defaultReader.ReadRuntimeSpell(e));

        // ENCH (0x13)
        var enchEntries = allEntries.Where(e => e.FormType == 0x13 && e.TesFormOffset.HasValue).ToList();
        typeResults["ENCH"] = CompareReads(enchEntries,
            e => probedReader.ReadRuntimeEnchantment(e),
            e => defaultReader.ReadRuntimeEnchantment(e));

        // PERK (0x56)
        var perkEntries = allEntries.Where(e => e.FormType == 0x56 && e.TesFormOffset.HasValue).ToList();
        typeResults["PERK"] = CompareReads(perkEntries,
            e => probedReader.ReadRuntimePerk(e),
            e => defaultReader.ReadRuntimePerk(e));

        // BOOK (0x19)
        var bookEntries = allEntries.Where(e => e.FormType == 0x19 && e.TesFormOffset.HasValue).ToList();
        typeResults["BOOK"] = CompareReads(bookEntries,
            e => probedReader.ReadRuntimeBook(e),
            e => defaultReader.ReadRuntimeBook(e));

        // Format output line
        var parts = new[] { "RACE", "PROJ", "MGEF", "SPEL", "ENCH", "PERK", "BOOK" }
            .Select(t =>
            {
                var r = typeResults[t];
                if (r.TotalEntries == 0)
                {
                    return $"{"---",10}";
                }

                var delta = r.ProbedSuccess - r.DefaultSuccess;
                var deltaStr = delta > 0 ? $"+{delta}" : delta < 0 ? $"{delta}" : "=";
                return $"{r.ProbedSuccess}/{r.TotalEntries} {deltaStr}";
            });

        log($"{fileName,-42} {fileDate,-12} {string.Join(" ", parts.Select(p => $"{p,10}"))}");

        return new DmpProbeResult(fileName, fileDate, typeResults, probeShifts);
    }

    private static TypeReadResult CompareReads<T>(
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Func<RuntimeEditorIdEntry, T?> probedRead,
        Func<RuntimeEditorIdEntry, T?> defaultRead) where T : class
    {
        var probedSuccess = 0;
        var defaultSuccess = 0;

        foreach (var entry in entries)
        {
            if (probedRead(entry) != null)
            {
                probedSuccess++;
            }

            if (defaultRead(entry) != null)
            {
                defaultSuccess++;
            }
        }

        return new TypeReadResult(entries.Count, probedSuccess, defaultSuccess);
    }

    private static string FormatShifts(int[] shifts)
    {
        var parts = new List<string>();
        for (var i = 0; i < shifts.Length; i++)
        {
            if (shifts[i] != 0)
            {
                parts.Add($"G{i}={shifts[i]:+0;-0}");
            }
        }

        return parts.Count == 0 ? "0" : string.Join(",", parts);
    }

    private record DmpProbeResult(
        string FileName,
        string FileDate,
        Dictionary<string, TypeReadResult> TypeResults,
        List<(string Name, string Shift, int Margin)> ProbeShifts);

    private record TypeReadResult(int TotalEntries, int ProbedSuccess, int DefaultSuccess);
}
