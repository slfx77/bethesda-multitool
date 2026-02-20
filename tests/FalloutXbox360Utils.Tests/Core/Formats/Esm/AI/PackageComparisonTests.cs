using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.AI;

/// <summary>
///     Diagnostic comparison of packages between memory dump and proto ESM.
///     Identifies discrepancies in type, flags, and schedule between the two sources.
/// </summary>
public sealed class PackageComparisonTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private static readonly string ResultFile = Path.Combine(
        Path.GetTempPath(), "package_comparison_results.txt");

    private void Log(string msg)
    {
        output.WriteLine(msg);
        File.AppendAllText(ResultFile, msg + Environment.NewLine);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ComparePackages_DumpVsProtoEsm_ReportsDiscrepancies()
    {
        await File.WriteAllTextAsync(ResultFile, $"Package Comparison - {DateTime.Now}\n\n",
            TestContext.Current.CancellationToken);
        Assert.SkipWhen(samples.Xbox360ProtoEsm is null, "Xbox 360 proto ESM not available");

        // Find latest memory dump
        var dumpDir = Path.GetDirectoryName(samples.ReleaseDump ?? samples.DebugDump);
        Assert.SkipWhen(dumpDir is null, "No memory dump available");

        // Try xex44 first (latest dump, most game data loaded), then fall back to largest
        var xex44 = Path.Combine(dumpDir, "Fallout_Release_Beta.xex44.dmp");
        string dumpPath;
        if (File.Exists(xex44))
        {
            dumpPath = xex44;
        }
        else
        {
            var dumpFiles = Directory.GetFiles(dumpDir, "Fallout_Release_Beta.xex*.dmp")
                .OrderByDescending(f => f.Length)
                .ToList();
            Assert.SkipWhen(dumpFiles.Count == 0, "No release beta dumps found");
            dumpPath = dumpFiles[0];
        }
        Log($"Using dump: {Path.GetFileName(dumpPath)} ({new FileInfo(dumpPath).Length:N0} bytes)");

        // Parse proto ESM packages
        Log("\n=== Loading proto ESM ===");
        var esmPackages = await LoadPackagesFromFile(samples.Xbox360ProtoEsm!);
        Log($"Proto ESM packages: {esmPackages.Count}");

        // Parse dump packages
        Log("\n=== Loading memory dump ===");
        var (dumpPackages, dumpDiag) = await LoadPackagesFromFileWithDiag(dumpPath);
        Log($"Dump packages: {dumpPackages.Count}");
        Log($"  ESM records found: {dumpDiag.esmRecordCount}");
        Log($"  PACK records in scan: {dumpDiag.packRecordCount}");
        Log($"  Runtime editor IDs: {dumpDiag.runtimeEditorIdCount}");
        Log($"  Runtime PACK entries: {dumpDiag.runtimePackCount}");
        Log($"  MinidumpInfo: {dumpDiag.hasMinidump}");
        Log($"  FormIdMap entries: {dumpDiag.formIdMapCount}");

        // Build lookup by FormID
        var esmByFormId = esmPackages
            .Where(p => p.FormId != 0)
            .GroupBy(p => p.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        var dumpByFormId = dumpPackages
            .Where(p => p.FormId != 0)
            .GroupBy(p => p.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Find matching FormIDs
        var matching = esmByFormId.Keys.Intersect(dumpByFormId.Keys).OrderBy(x => x).ToList();
        Log($"\nMatching FormIDs: {matching.Count}");

        // Compare
        int typeMismatches = 0;
        int flagMismatches = 0;
        int scheduleMismatches = 0;
        int totalWithData = 0;
        int dumpMissingData = 0;

        foreach (var formId in matching)
        {
            var esm = esmByFormId[formId];
            var dump = dumpByFormId[formId];

            if (dump.Data == null)
            {
                if (esm.Data != null)
                {
                    dumpMissingData++;
                }

                continue;
            }

            if (esm.Data == null)
            {
                continue;
            }

            totalWithData++;

            bool hasMismatch = false;

            // Compare Type
            if (esm.Data.Type != dump.Data.Type)
            {
                typeMismatches++;
                hasMismatch = true;
            }

            // Compare GeneralFlags
            if (esm.Data.GeneralFlags != dump.Data.GeneralFlags)
            {
                flagMismatches++;
                hasMismatch = true;
            }

            // Compare FO behavior and type-specific flags
            if (esm.Data.FalloutBehaviorFlags != dump.Data.FalloutBehaviorFlags ||
                esm.Data.TypeSpecificFlags != dump.Data.TypeSpecificFlags)
            {
                if (!hasMismatch)
                {
                    flagMismatches++;
                }

                hasMismatch = true;
            }

            // Compare schedule
            if (esm.Schedule != null && dump.Schedule != null &&
                (esm.Schedule.Month != dump.Schedule.Month ||
                 esm.Schedule.DayOfWeek != dump.Schedule.DayOfWeek ||
                 esm.Schedule.Time != dump.Schedule.Time ||
                 esm.Schedule.Duration != dump.Schedule.Duration))
            {
                scheduleMismatches++;
                hasMismatch = true;
            }

            if (hasMismatch && (typeMismatches + flagMismatches + scheduleMismatches) <= 30)
            {
                Log(
                    $"\n  MISMATCH 0x{formId:X8} ({esm.EditorId ?? dump.EditorId ?? "?"}):");
                Log(
                    $"    ESM  Type={esm.Data.Type} ({esm.TypeName}) Flags=0x{esm.Data.GeneralFlags:X8} FO=0x{esm.Data.FalloutBehaviorFlags:X4} TS=0x{esm.Data.TypeSpecificFlags:X4}");
                Log(
                    $"    Dump Type={dump.Data.Type} ({dump.TypeName}) Flags=0x{dump.Data.GeneralFlags:X8} FO=0x{dump.Data.FalloutBehaviorFlags:X4} TS=0x{dump.Data.TypeSpecificFlags:X4}");
                if (esm.Schedule != null || dump.Schedule != null)
                {
                    Log(
                        $"    ESM  Sched: {esm.Schedule?.Summary ?? "none"}");
                    Log(
                        $"    Dump Sched: {dump.Schedule?.Summary ?? "none"}");
                }
            }
        }

        // Categorize mismatches
        int dumpAllZeroed = 0;   // Dump has all PKDT fields zeroed, ESM has data
        int dumpGainedValues = 0; // Dump has non-zero values where ESM had zero
        int bothNonZeroDiff = 0;  // Both non-zero, but different
        int invalidTypes = 0;    // Types outside valid range (0-16)

        foreach (var formId in matching)
        {
            var esm = esmByFormId[formId];
            var dump = dumpByFormId[formId];
            if (esm.Data == null || dump.Data == null)
            {
                continue;
            }

            // Check for invalid dump type
            if (dump.Data.Type > 16)
            {
                invalidTypes++;
            }

            // Categorize PKDT field differences
            bool esmHasData = esm.Data.GeneralFlags != 0 || esm.Data.FalloutBehaviorFlags != 0 ||
                              esm.Data.TypeSpecificFlags != 0;
            bool dumpHasData = dump.Data.GeneralFlags != 0 || dump.Data.FalloutBehaviorFlags != 0 ||
                               dump.Data.TypeSpecificFlags != 0;

            if (esmHasData && !dumpHasData && esm.Data.Type == dump.Data.Type)
            {
                dumpAllZeroed++;
            }
            else if (!esmHasData && dumpHasData)
            {
                dumpGainedValues++;
            }
            else if (esmHasData && dumpHasData &&
                     (esm.Data.GeneralFlags != dump.Data.GeneralFlags ||
                      esm.Data.FalloutBehaviorFlags != dump.Data.FalloutBehaviorFlags ||
                      esm.Data.TypeSpecificFlags != dump.Data.TypeSpecificFlags))
            {
                bothNonZeroDiff++;
            }
        }

        // Type distribution in dump
        var dumpTypeDist = dumpPackages
            .Where(p => p.Data != null)
            .GroupBy(p => p.Data!.Type)
            .OrderBy(g => g.Key)
            .ToList();

        Log("\n=== Summary ===");
        Log($"Matching packages with data: {totalWithData}");
        Log($"Dump missing PKDT data: {dumpMissingData}");
        Log($"Type mismatches: {typeMismatches}");
        Log($"Flag mismatches: {flagMismatches}");
        Log($"Schedule mismatches: {scheduleMismatches}");

        Log("\n=== Mismatch Categories ===");
        Log($"Dump all-zeroed (same type, ESM has flags): {dumpAllZeroed}");
        Log($"Dump gained values (ESM was zero): {dumpGainedValues}");
        Log($"Both non-zero, different: {bothNonZeroDiff}");
        Log($"Invalid dump types (>16): {invalidTypes}");

        Log("\n=== Dump Package Type Distribution ===");
        foreach (var g in dumpTypeDist)
        {
            Log($"  Type {g.Key,2} ({PackageTypeName(g.Key)}): {g.Count()}");
        }

        Log($"\n=== Dump Package Sources (from diag) ===");
        Log($"  Runtime hash table entries (FormType 0x49): {dumpDiag.runtimePackCount}");
        Log($"  ESM PACK records in fragment scan: {dumpDiag.packRecordCount}");

        // Also report dump-only packages (runtime supplements)
        var dumpOnly = dumpByFormId.Keys.Except(esmByFormId.Keys).ToList();
        Log($"\nDump-only packages (runtime): {dumpOnly.Count}");
        var dumpOnlyWithData = dumpOnly.Where(f => dumpByFormId[f].Data != null).ToList();
        Log($"  with PKDT data: {dumpOnlyWithData.Count}");

        if (dumpOnlyWithData.Count > 0)
        {
            var typeDist = dumpOnlyWithData
                .Select(f => dumpByFormId[f].TypeName)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Take(10);
            Log("  Type distribution:");
            foreach (var g in typeDist)
            {
                Log($"    {g.Key}: {g.Count()}");
            }

            // Show first 10
            foreach (var f in dumpOnlyWithData.Take(10))
            {
                var p = dumpByFormId[f];
                Log(
                    $"    0x{f:X8} ({p.EditorId ?? "?"}) Type={p.Data!.Type} ({p.TypeName}) Flags=0x{p.Data.GeneralFlags:X8}");
            }
        }

        // Analyze dump packages standalone - look for "nonsensical" values
        Log("\n=== Dump Package Flag Analysis ===");
        // Corrected mask: bits 0-10, 12-13, 17-20, 21-28
        const uint knownGeneralBits = 0x1FFF37FF;
        const ushort knownFOBits = 0x01FF;         // All defined PackageFOBehaviorFlags bits
        const ushort knownTSBits = 0x017F;          // All defined PackageTypeSpecificFlags bits (incl. Allow Buying)

        int undefinedGeneralBits = 0;
        int undefinedFOBits = 0;
        int undefinedTSBits = 0;
        int noSchedule = 0;
        int noData = 0;
        int nameTypeMismatch = 0;      // EditorId contains type hint that doesn't match
        int uninitializedMemory = 0;   // 0xCDCD/0xCDCDCDCD pattern (MS CRT heap fill)

        var suspectPackages = new List<(PackageRecord pkg, string reason)>();

        foreach (var pkg in dumpPackages)
        {
            if (pkg.Data == null)
            {
                noData++;
                continue;
            }

            if (pkg.Schedule == null)
            {
                noSchedule++;
            }

            // Check for 0xCDCDCDCD / 0xCDCD uninitialized heap memory pattern
            bool hasUninit = false;
            if (pkg.Data.GeneralFlags == 0xCDCDCDCD ||
                pkg.Data.FalloutBehaviorFlags == 0xCDCD ||
                pkg.Data.TypeSpecificFlags == 0xCDCD)
            {
                uninitializedMemory++;
                hasUninit = true;
                if (suspectPackages.Count < 30)
                {
                    suspectPackages.Add((pkg,
                        $"UNINITIALIZED MEMORY (0xCD fill): Flags=0x{pkg.Data.GeneralFlags:X8} FO=0x{pkg.Data.FalloutBehaviorFlags:X4} TS=0x{pkg.Data.TypeSpecificFlags:X4}"));
                }
            }

            if (!hasUninit)
            {
                // Check for undefined bits in GeneralFlags
                var unknownGeneral = pkg.Data.GeneralFlags & ~knownGeneralBits;
                if (unknownGeneral != 0)
                {
                    undefinedGeneralBits++;
                    if (suspectPackages.Count < 30)
                    {
                        suspectPackages.Add((pkg, $"Undefined GeneralFlags bits: 0x{unknownGeneral:X8}"));
                    }
                }

                // Check for undefined bits in FO behavior flags
                var unknownFO = (ushort)(pkg.Data.FalloutBehaviorFlags & ~knownFOBits);
                if (unknownFO != 0)
                {
                    undefinedFOBits++;
                    if (suspectPackages.Count < 30)
                    {
                        suspectPackages.Add((pkg, $"Undefined FOBehavior bits: 0x{unknownFO:X4}"));
                    }
                }

                // Check for undefined bits in type-specific flags
                var unknownTS = (ushort)(pkg.Data.TypeSpecificFlags & ~knownTSBits);
                if (unknownTS != 0)
                {
                    undefinedTSBits++;
                    if (suspectPackages.Count < 30)
                    {
                        suspectPackages.Add((pkg, $"Undefined TypeSpecific bits: 0x{unknownTS:X4}"));
                    }
                }
            }

            // Check if EditorId contains a type hint that mismatches
            if (pkg.EditorId != null)
            {
                var edLower = pkg.EditorId.ToLowerInvariant();
                bool mismatch = false;
                if ((edLower.Contains("sleep") && pkg.Data.Type != 4) ||
                    (edLower.Contains("sandbox") && pkg.Data.Type != 12) ||
                    (edLower.Contains("patrol") && pkg.Data.Type != 13) ||
                    (edLower.Contains("guard") && pkg.Data.Type != 14 && !edLower.Contains("bodyguard")) ||
                    (edLower.Contains("travel") && pkg.Data.Type != 6) ||
                    (edLower.Contains("follow") && pkg.Data.Type != 1 && !edLower.Contains("following")))
                {
                    mismatch = true;
                }

                if (mismatch)
                {
                    nameTypeMismatch++;
                    if (suspectPackages.Count < 20)
                    {
                        suspectPackages.Add((pkg,
                            $"EditorId '{pkg.EditorId}' suggests different type than {pkg.Data.Type} ({pkg.TypeName})"));
                    }
                }
            }
        }

        Log($"Packages with no PKDT data: {noData}");
        Log($"Packages with no schedule: {noSchedule}");
        Log($"Uninitialized memory (0xCDCD pattern): {uninitializedMemory}");
        Log($"Packages with undefined GeneralFlags bits: {undefinedGeneralBits}");
        Log($"Packages with undefined FOBehavior bits: {undefinedFOBits}");
        Log($"Packages with undefined TypeSpecific bits: {undefinedTSBits}");
        Log($"EditorId/Type name mismatches: {nameTypeMismatch}");

        if (suspectPackages.Count > 0)
        {
            Log("\n=== Suspect Dump Packages ===");
            foreach (var (pkg, reason) in suspectPackages)
            {
                Log($"  0x{pkg.FormId:X8} ({pkg.EditorId ?? "?"}) Type={pkg.Data!.Type} ({pkg.TypeName})");
                Log($"    Flags=0x{pkg.Data.GeneralFlags:X8} FO=0x{pkg.Data.FalloutBehaviorFlags:X4} TS=0x{pkg.Data.TypeSpecificFlags:X4}");
                Log($"    Sched: {pkg.Schedule?.Summary ?? "none"}");
                Log($"    Reason: {reason}");
            }
        }

        Log($"\nResults written to: {ResultFile}");
    }

    private static string PackageTypeName(int type) => type switch
    {
        0 => "Find", 1 => "Follow", 2 => "Escort", 3 => "Eat", 4 => "Sleep",
        5 => "Wander", 6 => "Travel", 7 => "Accompany", 8 => "Use Item At",
        9 => "Ambush", 10 => "Flee Not Combat", 11 => "?11?",
        12 => "Sandbox", 13 => "Patrol", 14 => "Guard", 15 => "Dialogue",
        16 => "Use Weapon", _ => $"?{type}?"
    };

    private static async Task<List<PackageRecord>> LoadPackagesFromFile(string path)
    {
        var (packages, _) = await LoadPackagesFromFileWithDiag(path);
        return packages;
    }

    private static async Task<(List<PackageRecord> packages,
        (int esmRecordCount, int packRecordCount, int runtimeEditorIdCount,
            int runtimePackCount, bool hasMinidump, int formIdMapCount) diag)>
        LoadPackagesFromFileWithDiag(string path)
    {
        var progress = new Progress<AnalysisProgress>();

        // Use MinidumpAnalyzer for dump files, EsmFileAnalyzer for ESM files
        AnalysisResult analysisResult;
        if (path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            var analyzer = new MinidumpAnalyzer();
            analysisResult = await analyzer.AnalyzeAsync(path, progress,
                cancellationToken: CancellationToken.None);
        }
        else
        {
            analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, progress, CancellationToken.None);
        }

        if (analysisResult.EsmRecords == null)
        {
            return ([], (0, 0, 0, 0, analysisResult.MinidumpInfo != null, 0));
        }

        var scan = analysisResult.EsmRecords;
        var packCount = scan.MainRecords.Count(r => r.RecordType == "PACK");
        var runtimeTotal = scan.RuntimeEditorIds.Count;
        var runtimePack = scan.RuntimeEditorIds.Count(e => e.FormType == 0x49);

        var fileInfo = new FileInfo(path);
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileInfo.Length,
            analysisResult.MinidumpInfo);

        var packages = parser.ReconstructPackages();

        return (packages, (
            scan.MainRecords.Count,
            packCount,
            runtimeTotal,
            runtimePack,
            analysisResult.MinidumpInfo != null,
            analysisResult.FormIdMap?.Count ?? 0));
    }
}
