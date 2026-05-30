using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     v3 Phase 1 pre-flight: verifies that the (GridX, GridY) grid coordinates of every
///     exterior cell are identical across the four supported builds (360-final, July-2010
///     prototype, Aug-2010 prototype, PC-final) for cells that share a FormID.
///     <para>
///         If parity holds, the 3D camera's world coordinates are build-agnostic and can
///         use the same <c>CellSize × GridX/GridY</c> math regardless of which build's
///         ESM is loaded. If parity FAILS, the v3 plan grows a per-build origin offset.
///     </para>
///     <para>
///         This is real-data only because the question is fundamentally about whether
///         the shipping game files agree — a synthetic test would prove nothing. Bucket B
///         is the right gate even though new synthetic tests are normally preferred.
///     </para>
/// </summary>
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ExteriorCellCrossBuildParityTests(SampleFileFixture samples)
{
    [Fact]
    public async Task ExteriorCellGridCoords_ParityAcrossAllFourBuilds()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");
        Assert.SkipWhen(samples.Xbox360July2010Esm is null, "Xbox 360 July 2010 ESM not available");
        Assert.SkipWhen(samples.Xbox360Aug2010Esm is null, "Xbox 360 Aug 2010 ESM not available");

        var pc = await LoadExteriorCellsAsync(samples.PcFinalEsm!);
        var xboxFinal = await LoadExteriorCellsAsync(samples.Xbox360FinalEsm!);
        var july = await LoadExteriorCellsAsync(samples.Xbox360July2010Esm!);
        var aug = await LoadExteriorCellsAsync(samples.Xbox360Aug2010Esm!);

        var mismatches = new List<string>();
        var matchedXboxFinal = CompareBuilds("Xbox360Final", pc, xboxFinal, mismatches);
        var matchedJuly = CompareBuilds("Xbox360July2010", pc, july, mismatches);
        var matchedAug = CompareBuilds("Xbox360Aug2010", pc, aug, mismatches);

        // Report aggregate so the test output is useful when it skips or partially matches.
        var summary =
            $"PC-final exterior cells: {pc.Count}. " +
            $"Matched by FormID: Xbox360Final {matchedXboxFinal}, July2010 {matchedJuly}, Aug2010 {matchedAug}.";

        Assert.True(mismatches.Count == 0,
            $"{summary} Grid-coord mismatches:\n  " + string.Join("\n  ", mismatches.Take(20))
            + (mismatches.Count > 20 ? $"\n  ... and {mismatches.Count - 20} more" : ""));

        // Hard guarantee that at least SOME cross-build match occurred. If FormIDs don't
        // overlap at all the test would silently pass with zero comparisons.
        Assert.True(matchedXboxFinal > 1000,
            $"Xbox360Final matched only {matchedXboxFinal} cells with PC-final by FormID — too few. {summary}");
    }

    private static async Task<Dictionary<uint, (int GridX, int GridY)>> LoadExteriorCellsAsync(string path)
    {
        var source = await SemanticSourceSetBuilder.LoadSourceAsync(
            new SemanticSourceRequest { FilePath = path, FileType = AnalysisFileType.EsmFile });

        var exterior = new Dictionary<uint, (int, int)>(capacity: source.Records.Cells.Count);
        foreach (var cell in source.Records.Cells)
        {
            if (cell.GridX is int gx && cell.GridY is int gy)
                exterior[cell.FormId] = (gx, gy);
        }
        return exterior;
    }

    private static int CompareBuilds(
        string buildName,
        Dictionary<uint, (int GridX, int GridY)> pc,
        Dictionary<uint, (int GridX, int GridY)> other,
        List<string> mismatches)
    {
        var matched = 0;
        foreach (var (formId, pcCoords) in pc)
        {
            if (!other.TryGetValue(formId, out var otherCoords))
                continue;
            matched++;
            if (pcCoords != otherCoords)
            {
                mismatches.Add(
                    $"{buildName} FormID 0x{formId:X8}: PC=({pcCoords.GridX},{pcCoords.GridY}) " +
                    $"vs other=({otherCoords.GridX},{otherCoords.GridY})");
            }
        }
        return matched;
    }
}
