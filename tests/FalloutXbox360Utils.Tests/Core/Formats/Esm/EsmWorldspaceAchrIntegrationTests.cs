using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Integration tests verifying that the ESM analysis pipeline correctly places
///     ACHR/ACRE records into worldspace cells. Uses the PC retail FalloutNV.esm.
/// </summary>
public class EsmWorldspaceAchrIntegrationTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    ///     Verifies that the full pipeline (parse → GRUP mapping → cell reconstruction → worldspace linking)
    ///     correctly places persistent ACHR/ACRE records into worldspace cells.
    ///     Specifically checks for Veronica (ACHR 0x000E32A9) in the WastelandNV worldspace.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void WorldspaceReconstruction_PersistentCell_ShouldContainAchrRecords()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var pipeline = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!);
        var parsedRecords = pipeline.ParsedRecords;
        var scanResult = pipeline.ScanResult;
        var collection = pipeline.Collection;

        _output.WriteLine($"Parsed {parsedRecords.Count:N0} records, BigEndian={pipeline.IsBigEndian}");

        var achrParsed = parsedRecords.Count(r => r.Header.Signature == "ACHR");
        var acreParsed = parsedRecords.Count(r => r.Header.Signature == "ACRE");
        _output.WriteLine($"Raw parsed: ACHR={achrParsed:N0}, ACRE={acreParsed:N0}");

        _output.WriteLine($"ScanResult: {scanResult.MainRecords.Count:N0} main records, " +
                          $"{scanResult.RefrRecords.Count:N0} REFR/ACHR/ACRE records");

        var achrInScan = scanResult.RefrRecords.Count(r => r.Header.RecordType == "ACHR");
        var acreInScan = scanResult.RefrRecords.Count(r => r.Header.RecordType == "ACRE");
        _output.WriteLine($"RefrRecords: ACHR={achrInScan:N0}, ACRE={acreInScan:N0}");

        // Verify Veronica's ACHR is in the scan result
        var veronicaScan = scanResult.RefrRecords.FirstOrDefault(r => r.Header.FormId == 0x000E32A9);
        Assert.NotNull(veronicaScan);
        _output.WriteLine($"Veronica scan: FormId=0x{veronicaScan.Header.FormId:X8}, " +
                          $"Type={veronicaScan.Header.RecordType}, Base=0x{veronicaScan.BaseFormId:X8}, " +
                          $"Pos=({veronicaScan.Position?.X:F0}, {veronicaScan.Position?.Y:F0}, {veronicaScan.Position?.Z:F0})");

        // Check which cell Veronica maps to via CellToRefrMap
        uint? veronicaCellFormId = null;
        foreach (KeyValuePair<uint, List<uint>> entry in scanResult.CellToRefrMap)
        {
            if (entry.Value.Contains(0x000E32A9))
            {
                veronicaCellFormId = entry.Key;
                break;
            }
        }

        _output.WriteLine(veronicaCellFormId.HasValue
            ? $"Veronica mapped to cell 0x{veronicaCellFormId.Value:X8} via CellToRefrMap"
            : "WARNING: Veronica NOT found in any CellToRefrMap entry!");
        Assert.True(veronicaCellFormId.HasValue,
            "Veronica's ACHR (0x000E32A9) should be mapped to a cell via CellToRefrMap");

        // Check if Veronica's cell is linked to a worldspace
        scanResult.CellToWorldspaceMap.TryGetValue(veronicaCellFormId!.Value, out var veronicaWorldspace);
        _output.WriteLine(veronicaWorldspace != 0
            ? $"Veronica's cell linked to worldspace 0x{veronicaWorldspace:X8}"
            : "WARNING: Veronica's cell NOT linked to any worldspace!");

        // Full semantic reconstruction results
        _output.WriteLine($"Reconstructed: {collection.Cells.Count:N0} cells, " +
                          $"{collection.Worldspaces.Count:N0} worldspaces");

        // Find WastelandNV worldspace (0x000DA726)
        var wasteland = collection.Worldspaces.FirstOrDefault(w => w.FormId == 0x000DA726);
        Assert.NotNull(wasteland);
        _output.WriteLine($"WastelandNV: FormId=0x{wasteland.FormId:X8}, EditorId={wasteland.EditorId}, " +
                          $"Cells={wasteland.Cells.Count:N0}");

        // Check 188TradingPost cell
        var tradingPost = wasteland.Cells.FirstOrDefault(c => c.FormId == 0x000DDF1C);
        Assert.NotNull(tradingPost);
        Assert.Equal(7, tradingPost.GridX);
        Assert.Equal(7, tradingPost.GridY);
        _output.WriteLine($"188TradingPost: FormId=0x{tradingPost.FormId:X8}, " +
                          $"Grid=[{tradingPost.GridX}, {tradingPost.GridY}], " +
                          $"EditorId={tradingPost.EditorId}, " +
                          $"PlacedObjects={tradingPost.PlacedObjects.Count}");

        // Count ACHR/ACRE across all worldspace cells
        var totalAchr = 0;
        var totalAcre = 0;
        var cellsWithActors = 0;
        CellRecord? veronicaCell = null;

        foreach (var cell in wasteland.Cells)
        {
            var cellAchr = cell.PlacedObjects.Count(o => o.RecordType == "ACHR");
            var cellAcre = cell.PlacedObjects.Count(o => o.RecordType == "ACRE");
            totalAchr += cellAchr;
            totalAcre += cellAcre;

            if (cellAchr > 0 || cellAcre > 0)
            {
                cellsWithActors++;
            }

            var veronica = cell.PlacedObjects.FirstOrDefault(o => o.FormId == 0x000E32A9);
            if (veronica != null)
            {
                veronicaCell = cell;
                _output.WriteLine($"Veronica found in cell 0x{cell.FormId:X8} " +
                                  $"(Grid={cell.GridX},{cell.GridY}, EditorId={cell.EditorId}): " +
                                  $"RecordType={veronica.RecordType}, Base=0x{veronica.BaseFormId:X8}, " +
                                  $"Pos=({veronica.X:F0}, {veronica.Y:F0}, {veronica.Z:F0})");
            }
        }

        _output.WriteLine($"WastelandNV totals: ACHR={totalAchr:N0}, ACRE={totalAcre:N0}, " +
                          $"CellsWithActors={cellsWithActors:N0}");

        // Log cells with high ACHR counts (persistent cells typically hold many actors)
        var cellsWithManyActors = wasteland.Cells
            .Select(c => new
            {
                Cell = c,
                Achr = c.PlacedObjects.Count(o => o.RecordType == "ACHR"),
                Acre = c.PlacedObjects.Count(o => o.RecordType == "ACRE")
            })
            .Where(c => c.Achr >= 10 || c.Acre >= 10)
            .OrderByDescending(c => c.Achr + c.Acre)
            .Take(10)
            .ToList();
        _output.WriteLine("Top cells by actor count:");
        foreach (var c in cellsWithManyActors)
        {
            var refr = c.Cell.PlacedObjects.Count(o => o.RecordType == "REFR");
            _output.WriteLine($"  Cell 0x{c.Cell.FormId:X8} (Grid={c.Cell.GridX},{c.Cell.GridY}, " +
                              $"EditorId={c.Cell.EditorId}): REFR={refr}, ACHR={c.Achr}, ACRE={c.Acre}");
        }

        // Assertions
        Assert.NotNull(veronicaCell);
        var veronicaRef = veronicaCell.PlacedObjects.First(o => o.FormId == 0x000E32A9);
        Assert.Equal("ACHR", veronicaRef.RecordType);
        Assert.Equal(0x000E32AAu, veronicaRef.BaseFormId);

        Assert.True(totalAchr >= 100,
            $"Expected >= 100 ACHR in WastelandNV, got {totalAchr}");
        Assert.True(totalAcre >= 50,
            $"Expected >= 50 ACRE in WastelandNV, got {totalAcre}");

        // The cell containing Veronica (persistent cell 0x000846EA) should have many ACHR records
        var veronicaCellAchr = veronicaCell.PlacedObjects.Count(o => o.RecordType == "ACHR");
        Assert.True(veronicaCellAchr >= 50,
            $"Expected Veronica's cell to have >= 50 ACHR records, got {veronicaCellAchr}");
    }
}
