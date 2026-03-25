using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Tests verifying that the ESM analysis pipeline correctly places ACHR/ACRE records
///     into worldspace cells, using synthetic ESM data.
/// </summary>
public class EsmWorldspaceAchrIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void WorldspaceParsing_PersistentCell_ShouldContainAchrRecords()
    {
        // Build synthetic worldspace with Veronica, 188TradingPost, and enough ACHR/ACRE records
        var persistentRefs = new List<EsmTestFileBuilder.PlacedRefData>();

        // Veronica (ACHR 0x000E32A9, base NPC_ 0x000E32AA)
        persistentRefs.Add(new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "ACHR", FormId = 0x000E32A9, BaseFormId = 0x000E32AA,
            X = 2800, Y = 2800, Z = 100
        });

        // Generate 109 more ACHR records (total >= 110 in persistent cell, easily > 100 and > 50)
        for (uint i = 0; i < 109; i++)
        {
            persistentRefs.Add(new EsmTestFileBuilder.PlacedRefData
            {
                RecordType = "ACHR", FormId = 0x00200000 + i, BaseFormId = 0x00060001,
                X = 1000 + i * 10, Y = 1000, Z = 0
            });
        }

        // Generate 60 ACRE records (> 50 required)
        for (uint i = 0; i < 60; i++)
        {
            persistentRefs.Add(new EsmTestFileBuilder.PlacedRefData
            {
                RecordType = "ACRE", FormId = 0x00300000 + i, BaseFormId = 0x00070001,
                X = 2000 + i * 10, Y = 2000, Z = 0
            });
        }

        var builder = new EsmTestFileBuilder();
        builder.AddWorldspace(new EsmTestFileBuilder.WorldspaceData
        {
            FormId = 0x000DA726, // WastelandNV
            EditorId = "WastelandNV",
            FullName = "Mojave Wasteland",
            PersistentCell = new EsmTestFileBuilder.CellData
            {
                FormId = 0x000846EA,
                EditorId = "WastelandNVPersistent",
                PersistentRefs = persistentRefs
            },
            ExteriorCells =
            [
                new EsmTestFileBuilder.CellData
                {
                    FormId = 0x000DDF1C, // 188TradingPost
                    EditorId = "188TradingPost",
                    GridX = 7,
                    GridY = 7,
                    TemporaryRefs =
                    [
                        new EsmTestFileBuilder.PlacedRefData
                        {
                            RecordType = "REFR", FormId = 0x00400001, BaseFormId = 0x00050001,
                            X = 2800, Y = 2800, Z = 0
                        }
                    ]
                }
            ]
        });

        var pipeline = builder.BuildAndAnalyze();
        var parsedRecords = pipeline.ParsedRecords;
        var scanResult = pipeline.ScanResult;
        var collection = pipeline.Collection;

        _output.WriteLine($"Parsed {parsedRecords.Count} records");

        var achrParsed = parsedRecords.Count(r => r.Header.Signature == "ACHR");
        var acreParsed = parsedRecords.Count(r => r.Header.Signature == "ACRE");
        _output.WriteLine($"Raw: ACHR={achrParsed}, ACRE={acreParsed}");

        // Verify Veronica in scan result
        var veronicaScan = scanResult.RefrRecords.FirstOrDefault(r => r.Header.FormId == 0x000E32A9);
        Assert.NotNull(veronicaScan);

        // Verify CellToRefrMap
        uint? veronicaCellFormId = null;
        foreach (var entry in scanResult.CellToRefrMap)
        {
            if (entry.Value.Contains(0x000E32A9))
            {
                veronicaCellFormId = entry.Key;
                break;
            }
        }

        Assert.True(veronicaCellFormId.HasValue, "Veronica should be mapped to a cell");

        // Verify WastelandNV worldspace
        var wasteland = collection.Worldspaces.FirstOrDefault(w => w.FormId == 0x000DA726);
        Assert.NotNull(wasteland);
        _output.WriteLine($"WastelandNV cells: {wasteland.Cells.Count}");

        // Verify 188TradingPost cell
        var tradingPost = wasteland.Cells.FirstOrDefault(c => c.FormId == 0x000DDF1C);
        Assert.NotNull(tradingPost);
        Assert.Equal(7, tradingPost.GridX);
        Assert.Equal(7, tradingPost.GridY);

        // Count actors across all cells
        var totalAchr = wasteland.Cells.Sum(c => c.PlacedObjects.Count(o => o.RecordType == "ACHR"));
        var totalAcre = wasteland.Cells.Sum(c => c.PlacedObjects.Count(o => o.RecordType == "ACRE"));
        _output.WriteLine($"WastelandNV: ACHR={totalAchr}, ACRE={totalAcre}");

        Assert.True(totalAchr >= 100, $"Expected >= 100 ACHR, got {totalAchr}");
        Assert.True(totalAcre >= 50, $"Expected >= 50 ACRE, got {totalAcre}");

        // Find Veronica in her cell
        var veronicaCell = wasteland.Cells.FirstOrDefault(c =>
            c.PlacedObjects.Any(o => o.FormId == 0x000E32A9));
        Assert.NotNull(veronicaCell);

        var veronicaRef = veronicaCell.PlacedObjects.First(o => o.FormId == 0x000E32A9);
        Assert.Equal("ACHR", veronicaRef.RecordType);
        Assert.Equal(0x000E32AAu, veronicaRef.BaseFormId);

        var veronicaCellAchr = veronicaCell.PlacedObjects.Count(o => o.RecordType == "ACHR");
        Assert.True(veronicaCellAchr >= 50, $"Expected >= 50 ACHR in persistent cell, got {veronicaCellAchr}");
    }
}