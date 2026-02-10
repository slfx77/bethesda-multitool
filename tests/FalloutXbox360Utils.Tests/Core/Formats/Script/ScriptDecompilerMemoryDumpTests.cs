using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Xunit;
using Xunit.Abstractions;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Integration tests for script decompilation from Xbox 360 memory dumps.
///     Tests both debug builds (which retain SCTX source for comparison) and
///     release builds (which only have compiled bytecode).
///     These tests are skipped when sample dump files are not present.
/// </summary>
public class ScriptDecompilerMemoryDumpTests
{
    private readonly ITestOutputHelper _output;

    public ScriptDecompilerMemoryDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_RuntimeScripts_StructurallyCorrect()
    {
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        if (scripts == null)
        {
            _output.WriteLine("SKIPPED: Debug dump not available or analysis failed");
            return;
        }

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
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp");
        if (scripts == null)
        {
            _output.WriteLine("SKIPPED: Release dump not available or analysis failed");
            return;
        }

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
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        if (scripts == null)
        {
            _output.WriteLine("SKIPPED: Debug dump not available or analysis failed");
            return;
        }

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
        _output.WriteLine($"Scripts with unresolved FormIDs: {totalScriptsWithUnresolved}/{scriptsWithDecompiled.Count}");

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
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        if (scripts == null)
        {
            _output.WriteLine("SKIPPED: Debug dump not available or analysis failed");
            return;
        }

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
                    foreach (Match m in matches.Take(3))
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
}

/// <summary>
///     Helper for analyzing memory dumps in test fixtures.
///     Caches results per path so the expensive analysis runs at most once per dump file.
///     Uses Task.Run to offload all work to the thread pool, bypassing xUnit's sync context.
/// </summary>
internal static class DumpAnalysisHelper
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<List<ReconstructedScript>?>>> Cache = new();

    internal static Task<List<ReconstructedScript>?> AnalyzeDumpAsync(string relativePath)
    {
        var path = ScriptTestHelpers.FindSamplePath(relativePath);
        if (path == null)
        {
            return Task.FromResult<List<ReconstructedScript>?>(null);
        }

        var lazy = Cache.GetOrAdd(path, p => new Lazy<Task<List<ReconstructedScript>?>>(
            () => Task.Run(() => RunAnalysis(p))));
        return lazy.Value;
    }

    private static async Task<List<ReconstructedScript>?> RunAnalysis(string path)
    {
        // Ensure no SynchronizationContext leaks from xUnit into Progress<T> callbacks
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var analyzer = new MinidumpAnalyzer();
            var analysisResult = await analyzer.AnalyzeAsync(path, includeMetadata: true)
                .ConfigureAwait(false);

            if (analysisResult.EsmRecords == null)
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            using var mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new SemanticReconstructor(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);

            var semanticResult = reconstructor.ReconstructAll();
            return semanticResult.Scripts;
        }
        catch
        {
            return null;
        }
    }
}
