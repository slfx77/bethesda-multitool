using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     Ground-truth validation of TES4 Obscript decompilation: Oblivion.esm ships SCTX source next
///     to the compiled SCDA, so the decompiled output's block structure can be compared against the
///     author's own text for every shipped script. The threshold is a RATCHET — raise it as TES4
///     dialect fixes land; it exists to catch regressions, not to bless the current tail.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class OblivionScriptDecompilationIntegrationTests
{
    // Ratchet guard: 99.0% (2368/2393) as of the first Oblivion decompile pass, zero decode
    // errors — TES4's bytecode stream matches the FNV-era machinery. The residual ~1% includes
    // shipped SCTX/SCDA drift (compiled blocks absent from the shipped source text).
    private const double MinStructuralMatchRatio = 0.98;

    private static string? ResolveOblivionEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Oblivion.esm")))
        {
            return Path.Combine(root, "Oblivion.esm");
        }

        const string steam = @"E:\SteamLibrary\SteamApps\common\Oblivion\Data\Oblivion.esm";
        return File.Exists(steam) ? steam : null;
    }

    [Fact]
    public async Task OblivionScripts_DecompileWithStructuralFidelity()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = ResolveOblivionEsm();
        Assert.SkipUnless(esm is not null,
            "Oblivion.esm not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var scripts = result.Records.Scripts;
        Assert.True(scripts.Count > 500,
            $"Expected Oblivion.esm's SCPT records to surface on the schema-primary path; got {scripts.Count}.");

        var comparable = 0;
        var matched = 0;
        var errors = 0;
        var mismatchSamples = new List<string>();

        foreach (var script in scripts)
        {
            if (string.IsNullOrWhiteSpace(script.SourceText) ||
                string.IsNullOrWhiteSpace(script.DecompiledText))
            {
                continue;
            }

            comparable++;
            if (script.DecompiledText.Contains("; Decompilation error", StringComparison.Ordinal) ||
                script.DecompiledText.Contains("; Error decoding", StringComparison.Ordinal))
            {
                errors++;
            }

            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.SourceText);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.DecompiledText);
            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                matched++;
            }
            else if (mismatchSamples.Count < 5)
            {
                mismatchSamples.Add(
                    $"{script.EditorId ?? script.FormId.ToString("X8")}: " +
                    $"src[{string.Join(",", sourceStructure.Take(12))}] vs " +
                    $"dec[{string.Join(",", decompiledStructure.Take(12))}] | " +
                    ScriptTestHelpers.GetFirstErrorLine(script.DecompiledText));
            }
        }

        Assert.True(comparable > 500, $"Expected SCTX+SCDA pairs to compare; got {comparable}.");

        var ratio = (double)matched / comparable;
        Assert.True(ratio >= MinStructuralMatchRatio,
            $"Structural match {matched}/{comparable} = {ratio:P1} (< {MinStructuralMatchRatio:P0}); " +
            $"{errors} with decode errors.\nWorst samples:\n  " + string.Join("\n  ", mismatchSamples));
    }
}