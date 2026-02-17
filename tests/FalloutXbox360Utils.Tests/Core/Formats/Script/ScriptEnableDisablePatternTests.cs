using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Catalogs Enable/Disable/PlaceAtMe/AddScriptPackage patterns in decompiled scripts
///     from Xbox 360 memory dumps. These patterns reveal how the engine dynamically controls
///     actor placement and visibility at runtime.
/// </summary>
public class ScriptEnableDisablePatternTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Decompile_DebugDump_CatalogEnableDisablePatterns()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var scripts = await DumpAnalysisHelper.AnalyzeDumpAsync(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");
        Assert.SkipWhen(scripts is null, "Debug dump analysis failed");

        var scriptsWithDecompiled = scripts
            .Where(s => !string.IsNullOrEmpty(s.DecompiledText))
            .ToList();

        _output.WriteLine($"Total decompiled scripts: {scriptsWithDecompiled.Count}");

        if (scriptsWithDecompiled.Count == 0)
        {
            _output.WriteLine("No decompiled scripts found");
            return;
        }

        // Count scripts containing key actor-placement functions
        var patterns = new Dictionary<string, Regex>
        {
            ["Enable"] = new(@"\bEnable\b", RegexOptions.IgnoreCase),
            ["Disable"] = new(@"\bDisable\b", RegexOptions.IgnoreCase),
            ["PlaceAtMe"] = new(@"\bPlaceAtMe\b", RegexOptions.IgnoreCase),
            ["AddScriptPackage"] = new(@"\bAddScriptPackage\b", RegexOptions.IgnoreCase),
            ["EvaluatePackage"] = new(@"\b(?:EvaluatePackage|evp)\b", RegexOptions.IgnoreCase),
            ["MoveTo"] = new(@"\b(?:MoveTo|MoveToMarker)\b", RegexOptions.IgnoreCase),
            ["GetDisabled"] = new(@"\bGetDisabled\b", RegexOptions.IgnoreCase)
        };

        var results = new Dictionary<string, List<ScriptRecord>>();
        foreach (var (name, _) in patterns)
        {
            results[name] = [];
        }

        foreach (var script in scriptsWithDecompiled)
        {
            foreach (var (name, regex) in patterns)
            {
                if (regex.IsMatch(script.DecompiledText!))
                {
                    results[name].Add(script);
                }
            }
        }

        _output.WriteLine("\n=== Actor Placement Function Usage ===");
        foreach (var (name, matches) in results.OrderByDescending(kv => kv.Value.Count))
        {
            _output.WriteLine($"  {name,-22} {matches.Count,4} scripts");
        }

        // Log first few examples of Enable/Disable usage
        _output.WriteLine("\n--- Enable examples ---");
        foreach (var s in results["Enable"].Take(5))
        {
            _output.WriteLine($"  {s.EditorId ?? $"0x{s.FormId:X8}"}");
        }

        _output.WriteLine("\n--- Disable examples ---");
        foreach (var s in results["Disable"].Take(5))
        {
            _output.WriteLine($"  {s.EditorId ?? $"0x{s.FormId:X8}"}");
        }

        _output.WriteLine("\n--- PlaceAtMe examples ---");
        foreach (var s in results["PlaceAtMe"].Take(5))
        {
            _output.WriteLine($"  {s.EditorId ?? $"0x{s.FormId:X8}"}");
        }

        // Count scripts with OnPackageStart/OnPackageDone/OnPackageChange blocks
        var packageBlockPattern = new Regex(@"\bBegin\s+(?:OnPackageStart|OnPackageDone|OnPackageChange)\b",
            RegexOptions.IgnoreCase);
        var packageBlockScripts = scriptsWithDecompiled
            .Where(s => packageBlockPattern.IsMatch(s.DecompiledText!))
            .ToList();
        _output.WriteLine($"\nScripts with package event blocks: {packageBlockScripts.Count}");

        Assert.True(scriptsWithDecompiled.Count > 0, "Should have decompiled scripts");
        Assert.True(results["Enable"].Count + results["Disable"].Count > 0,
            "Should find at least some Enable or Disable calls in decompiled scripts");
    }
}
