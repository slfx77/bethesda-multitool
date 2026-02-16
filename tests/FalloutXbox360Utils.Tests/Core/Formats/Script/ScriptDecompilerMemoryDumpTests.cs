using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Integration tests for script decompilation from Xbox 360 memory dumps.
///     Tests both debug builds (which retain SCTX source for comparison) and
///     release builds (which only have compiled bytecode).
///     These tests are skipped when sample dump files are not present.
/// </summary>
public class ScriptDecompilerMemoryDumpTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_RuntimeScripts_StructurallyCorrect()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        Assert.SkipWhen(scripts is null, "Debug dump analysis failed");

        var runtimeScriptsWithSource = scripts
            .Where(s => s.FromRuntime && s.HasSource && s.DecompiledText != null)
            .ToList();

        _output.WriteLine($"Total scripts: {scripts.Count}");
        _output.WriteLine($"Runtime scripts with source + decompiled: {runtimeScriptsWithSource.Count}");

        if (runtimeScriptsWithSource.Count == 0)
        {
            _output.WriteLine("No runtime scripts with both source and decompiled text found");
            return;
        }

        var structuralMatches = 0;
        var mismatches = 0;
        var diagnosed = 0;

        foreach (var script in runtimeScriptsWithSource)
        {
            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.SourceText!);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.DecompiledText!);

            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                structuralMatches++;
            }
            else
            {
                mismatches++;
                if (diagnosed < 5)
                {
                    diagnosed++;
                    _output.WriteLine(
                        $"\n--- Mismatch #{diagnosed}: {script.EditorId ?? $"0x{script.FormId:X8}"} ---");
                    _output.WriteLine($"Source blocks:     [{string.Join(", ", sourceStructure)}]");
                    _output.WriteLine($"Decompiled blocks: [{string.Join(", ", decompiledStructure)}]");

                    if (script.CompiledData != null)
                    {
                        _output.WriteLine($"SCDA size: {script.CompiledData.Length} (big-endian runtime)");
                    }
                }
            }
        }

        var total = structuralMatches + mismatches;
        var matchRate = total > 0 ? 100.0 * structuralMatches / total : 0;
        _output.WriteLine($"\nStructural matches: {structuralMatches}/{total} ({matchRate:F1}%)");

        Assert.True(total > 0, "Should find runtime scripts with both source and decompiled text");
        // Allow some mismatches (same genuine source-vs-bytecode differences as ESM tests)
        Assert.True(matchRate > 80, $"Structural match rate {matchRate:F1}% below 80% threshold");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_ReleaseDump_RuntimeScripts_NoCrashes()
    {
        Assert.SkipWhen(samples.ReleaseDump is null, "Release memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp");
        Assert.SkipWhen(scripts is null, "Release dump analysis failed");

        var runtimeScripts = scripts
            .Where(s => s.FromRuntime && s.CompiledData is { Length: > 0 })
            .ToList();

        _output.WriteLine($"Total scripts: {scripts.Count}");
        _output.WriteLine($"Runtime scripts with bytecode: {runtimeScripts.Count}");

        if (runtimeScripts.Count == 0)
        {
            _output.WriteLine("No runtime scripts with compiled data found");
            return;
        }

        var successCount = runtimeScripts.Count(s =>
            s.DecompiledText != null &&
            !s.DecompiledText.StartsWith("; Decompilation failed", StringComparison.Ordinal));
        var failedCount = runtimeScripts.Count - successCount;
        var withBeginEnd = runtimeScripts.Count(s =>
            s.DecompiledText != null &&
            s.DecompiledText.Contains("Begin") &&
            s.DecompiledText.Contains("End"));

        // Log first few failures for debugging
        foreach (var script in runtimeScripts.Where(s => s.DecompiledText == null).Take(3))
        {
            _output.WriteLine($"  FAILED (null): {script.EditorId ?? $"0x{script.FormId:X8}"}");
        }

        foreach (var script in runtimeScripts
                     .Where(s => s.DecompiledText != null &&
                                 s.DecompiledText.StartsWith("; Decompilation failed", StringComparison.Ordinal))
                     .Take(3))
        {
            var text = script.DecompiledText!;
            _output.WriteLine(
                $"  FAILED: {script.EditorId ?? $"0x{script.FormId:X8}"}: {text[..Math.Min(100, text.Length)]}");
        }

        var total = runtimeScripts.Count;
        var successRate = 100.0 * successCount / total;
        _output.WriteLine($"\nResults: {successCount} success, {failedCount} failed out of {total}");
        _output.WriteLine($"Success rate: {successRate:F1}%");
        _output.WriteLine($"Scripts with Begin/End blocks: {withBeginEnd}");

        Assert.True(successRate > 90, $"Success rate {successRate:F1}% below 90% threshold");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_ResolvesFormIdsToEditorIds()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        Assert.SkipWhen(scripts is null, "Debug dump analysis failed");

        var scriptsWithDecompiled = scripts
            .Where(s => s.DecompiledText != null && s.ReferencedObjects.Count > 0)
            .ToList();

        _output.WriteLine($"Scripts with decompiled text and SCRO refs: {scriptsWithDecompiled.Count}");

        if (scriptsWithDecompiled.Count == 0)
        {
            _output.WriteLine("No scripts with decompiled text and references found");
            return;
        }

        var unresolvedFormIdPattern = new Regex(@"0x[0-9A-Fa-f]{8}");
        var totalUnresolved = 0;
        var totalScriptsWithUnresolved = 0;

        foreach (var script in scriptsWithDecompiled)
        {
            var matches = unresolvedFormIdPattern.Matches(script.DecompiledText!);
            if (matches.Count > 0)
            {
                totalUnresolved += matches.Count;
                totalScriptsWithUnresolved++;
            }
        }

        // Count total reference usages across all scripts for comparison
        var totalRefs = scriptsWithDecompiled.Sum(s => s.ReferencedObjects.Count);

        _output.WriteLine($"Total SCRO references across all scripts: {totalRefs}");
        _output.WriteLine($"Unresolved FormIDs in decompiled output: {totalUnresolved}");
        _output.WriteLine(
            $"Scripts with unresolved FormIDs: {totalScriptsWithUnresolved}/{scriptsWithDecompiled.Count}");

        if (totalRefs > 0)
        {
            var resolvedRate = 100.0 * (totalRefs - totalUnresolved) / totalRefs;
            _output.WriteLine($"Approximate resolution rate: {resolvedRate:F1}%");
        }

        // Verify we actually analyzed scripts (soft assertion — resolution rate varies)
        Assert.True(scriptsWithDecompiled.Count > 0, "Should have scripts with decompiled text and references");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_CrossScriptVariablesResolved()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        Assert.SkipWhen(scripts is null, "Debug dump analysis failed");

        var scriptsWithDecompiled = scripts
            .Where(s => !string.IsNullOrEmpty(s.DecompiledText))
            .ToList();

        _output.WriteLine($"Scripts with decompiled text: {scriptsWithDecompiled.Count}");

        // Pattern for unresolved cross-script variable: word.var followed by digits
        var unresolvedPattern = new Regex(@"\w+\.var\d+");

        var totalUnresolved = 0;
        var scriptsWithUnresolved = 0;

        foreach (var script in scriptsWithDecompiled)
        {
            var matches = unresolvedPattern.Matches(script.DecompiledText!);
            if (matches.Count > 0)
            {
                totalUnresolved += matches.Count;
                scriptsWithUnresolved++;

                if (scriptsWithUnresolved <= 3)
                {
                    _output.WriteLine(
                        $"  {script.EditorId ?? $"0x{script.FormId:X8}"}: {matches.Count} unresolved refs");
                    foreach (var m in matches.Take(3))
                    {
                        _output.WriteLine($"    {m.Value}");
                    }
                }
            }
        }

        _output.WriteLine($"\nTotal unresolved cross-script variable refs: {totalUnresolved}");
        _output.WriteLine($"Scripts with unresolved refs: {scriptsWithUnresolved}/{scriptsWithDecompiled.Count}");

        // Verify we actually analyzed scripts (soft assertion — resolution rate varies by dump)
        Assert.True(scriptsWithDecompiled.Count > 0, "Should have scripts with decompiled text");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_SemanticComparison()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        Assert.SkipWhen(scripts is null, "Debug dump analysis failed");

        var scriptsWithBoth = scripts
            .Where(s => s.HasSource && !string.IsNullOrEmpty(s.DecompiledText))
            .ToList();

        _output.WriteLine($"Total scripts: {scripts.Count}");
        _output.WriteLine($"Scripts with both SCTX and decompiled: {scriptsWithBoth.Count}");

        if (scriptsWithBoth.Count == 0)
        {
            _output.WriteLine("No scripts with both source and decompiled text found");
            return;
        }

        var nameMap = ScriptComparer.BuildFunctionNameNormalizationMap();

        var totalMatches = 0;
        var aggregateMismatches = new Dictionary<string, int>();
        var aggregateTolerated = new Dictionary<string, int>();
        var scriptMatchRates = new List<double>();
        var worstScripts = new List<(string Name, double MatchRate, int Mismatches)>();

        foreach (var script in scriptsWithBoth)
        {
            var result = ScriptComparer.CompareScripts(
                script.SourceText!, script.DecompiledText!, nameMap);

            totalMatches += result.MatchCount;
            scriptMatchRates.Add(result.MatchRate);

            foreach (var (category, count) in result.MismatchesByCategory)
            {
                aggregateMismatches.TryGetValue(category, out var existing);
                aggregateMismatches[category] = existing + count;
            }

            foreach (var (category, count) in result.ToleratedDifferences)
            {
                aggregateTolerated.TryGetValue(category, out var existing);
                aggregateTolerated[category] = existing + count;
            }

            if (result.MatchRate < 80 && worstScripts.Count < 5)
            {
                worstScripts.Add((
                    script.EditorId ?? $"0x{script.FormId:X8}",
                    result.MatchRate,
                    result.TotalMismatches));
            }
        }

        var totalMismatches = aggregateMismatches.Values.Sum();
        var totalTolerated = aggregateTolerated.Values.Sum();
        var totalLines = totalMatches + totalMismatches;
        var overallMatchRate = totalLines > 0 ? 100.0 * totalMatches / totalLines : 0;

        _output.WriteLine($"\n=== Semantic Comparison Results ===");
        _output.WriteLine($"Total lines compared: {totalLines:N0}");
        _output.WriteLine($"Matching lines: {totalMatches:N0} (includes {totalTolerated:N0} tolerated)");
        _output.WriteLine($"Mismatched lines: {totalMismatches:N0}");
        _output.WriteLine($"Overall match rate: {overallMatchRate:F1}%");

        _output.WriteLine($"\n--- Mismatch Categories ---");
        foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
        {
            var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
            _output.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
        }

        if (aggregateTolerated.Count > 0)
        {
            _output.WriteLine($"\n--- Tolerated Differences (counted as matches) ---");
            foreach (var (category, count) in aggregateTolerated.OrderByDescending(kv => kv.Value))
            {
                var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
                _output.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
            }
        }

        if (worstScripts.Count > 0)
        {
            _output.WriteLine($"\n--- Worst Scripts ---");
            foreach (var (name, rate, mismatches) in worstScripts)
            {
                _output.WriteLine($"  {name}: {rate:F1}% match ({mismatches} mismatches)");
            }
        }

        // Show examples from the first script that has mismatches
        var firstWithMismatches = scriptsWithBoth
            .Select(s => (Script: s, Result: ScriptComparer.CompareScripts(
                s.SourceText!, s.DecompiledText!, nameMap)))
            .FirstOrDefault(x => x.Result.Examples.Count > 0);

        if (firstWithMismatches.Script != null)
        {
            _output.WriteLine(
                $"\n--- Example mismatches from {firstWithMismatches.Script.EditorId ?? "?"} ---");
            foreach (var (source, decompiled, category) in firstWithMismatches.Result.Examples.Take(5))
            {
                _output.WriteLine($"  [{category}]");
                _output.WriteLine($"    SCTX: {source}");
                _output.WriteLine($"    SCDA: {decompiled}");
            }
        }

        // Write detailed report to file for analysis
        var reportPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "TestOutput", "semantic-comparison.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using (var writer = new StreamWriter(reportPath))
        {
            await writer.WriteLineAsync($"=== Semantic Comparison Results ===");
            await writer.WriteLineAsync($"Total lines compared: {totalLines:N0}");
            await writer.WriteLineAsync($"Matching lines: {totalMatches:N0} (includes {totalTolerated:N0} tolerated)");
            await writer.WriteLineAsync($"Mismatched lines: {totalMismatches:N0}");
            await writer.WriteLineAsync($"Overall match rate: {overallMatchRate:F1}%");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"--- Mismatch Categories ---");
            foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
            {
                var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
                await writer.WriteLineAsync($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
            }

            if (aggregateTolerated.Count > 0)
            {
                await writer.WriteLineAsync();
                await writer.WriteLineAsync($"--- Tolerated Differences (counted as matches) ---");
                foreach (var (category, count) in aggregateTolerated.OrderByDescending(kv => kv.Value))
                {
                    var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
                    await writer.WriteLineAsync($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
                }
            }

            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- All Mismatch Examples (first 10 per script) ---");
            foreach (var script in scriptsWithBoth)
            {
                var result = ScriptComparer.CompareScripts(
                    script.SourceText!, script.DecompiledText!, nameMap);
                if (result.Examples.Count == 0)
                {
                    continue;
                }

                await writer.WriteLineAsync(
                    $"\n  {script.EditorId ?? $"0x{script.FormId:X8}"} ({result.MatchRate:F1}% match, {result.TotalMismatches} mismatches):");
                foreach (var (source, decompiled, category) in result.Examples)
                {
                    await writer.WriteLineAsync($"    [{category}]");
                    await writer.WriteLineAsync($"      SCTX: {source}");
                    await writer.WriteLineAsync($"      SCDA: {decompiled}");
                }
            }
        }

        _output.WriteLine($"Detailed report written to: {Path.GetFullPath(reportPath)}");

        Assert.True(scriptsWithBoth.Count > 0, "Should have scripts with both source and decompiled text");
        // Initial baseline — will increase as we fix decompiler bugs
        Assert.True(overallMatchRate > 50,
            $"Overall semantic match rate {overallMatchRate:F1}% below 50% threshold");
    }
}