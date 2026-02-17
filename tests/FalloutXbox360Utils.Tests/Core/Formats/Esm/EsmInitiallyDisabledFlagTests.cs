using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Integration tests verifying that the Initially Disabled flag (0x0800) and
///     XESP (Enable Parent) data flow through the full ESM analysis pipeline
///     from raw records to reconstructed PlacedReference objects.
/// </summary>
public class EsmInitiallyDisabledFlagTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Slow")]
    public void WorldspaceReconstruction_InitiallyDisabledFlag_ShouldBeTracked()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var pipeline = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!);
        var parsedRecords = pipeline.ParsedRecords;
        var scanResult = pipeline.ScanResult;
        var collection = pipeline.Collection;

        _output.WriteLine($"Parsed {parsedRecords.Count:N0} records, BigEndian={pipeline.IsBigEndian}");

        // Step 1: Count records with Initially Disabled flag (0x0800) at the raw level
        var disabledRecords = parsedRecords
            .Where(r => (r.Header.Flags & 0x0800) != 0)
            .ToList();
        var disabledAchr = disabledRecords.Count(r => r.Header.Signature == "ACHR");
        var disabledAcre = disabledRecords.Count(r => r.Header.Signature == "ACRE");
        var disabledRefr = disabledRecords.Count(r => r.Header.Signature == "REFR");
        _output.WriteLine($"Initially Disabled raw records: total={disabledRecords.Count:N0} " +
                          $"(ACHR={disabledAchr:N0}, ACRE={disabledAcre:N0}, REFR={disabledRefr:N0})");

        Assert.True(disabledRecords.Count > 0,
            "Should find records with Initially Disabled flag (0x0800)");

        // Step 2: Verify Initially Disabled on ExtractedRefrRecord
        _output.WriteLine($"Extracted {scanResult.RefrRecords.Count:N0} REFR/ACHR/ACRE records");
        var disabledRefrs = scanResult.RefrRecords
            .Where(r => r.Header.IsInitiallyDisabled)
            .ToList();
        _output.WriteLine($"ExtractedRefrRecords with IsInitiallyDisabled: {disabledRefrs.Count:N0}");
        Assert.True(disabledRefrs.Count > 0,
            "Some ExtractedRefrRecords should have IsInitiallyDisabled");

        // Step 3: Verify XESP (Enable Parent) on ExtractedRefrRecord
        var withEnableParent = scanResult.RefrRecords
            .Where(r => r.EnableParentFormId is > 0)
            .ToList();
        _output.WriteLine($"ExtractedRefrRecords with EnableParentFormId: {withEnableParent.Count:N0}");
        Assert.True(withEnableParent.Count > 0,
            "Some ExtractedRefrRecords should have EnableParentFormId from XESP subrecord");

        // Log a few examples of each
        _output.WriteLine("\n--- Disabled ACHR/ACRE examples ---");
        foreach (var r in disabledRefrs.Where(r => r.Header.RecordType is "ACHR" or "ACRE").Take(5))
        {
            _output.WriteLine($"  {r.Header.RecordType} 0x{r.Header.FormId:X8} " +
                              $"Base=0x{r.BaseFormId:X8} Flags=0x{r.Header.Flags:X8}");
        }

        _output.WriteLine("\n--- Enable Parent examples ---");
        foreach (var r in withEnableParent.Take(5))
        {
            _output.WriteLine($"  {r.Header.RecordType} 0x{r.Header.FormId:X8} " +
                              $"EnableParent=0x{r.EnableParentFormId:X8} Flags={r.EnableParentFlags}");
        }

        // Step 4: Full reconstruction results
        _output.WriteLine($"\nReconstructed: {collection.Cells.Count:N0} cells, " +
                          $"{collection.Worldspaces.Count:N0} worldspaces");

        // Step 5: Verify flags on PlacedReference after reconstruction
        var allPlacedObjects = collection.Worldspaces
            .SelectMany(w => w.Cells)
            .SelectMany(c => c.PlacedObjects)
            .ToList();
        _output.WriteLine($"Total PlacedReference objects: {allPlacedObjects.Count:N0}");

        var disabledPlaced = allPlacedObjects.Where(p => p.IsInitiallyDisabled).ToList();
        var enableParentPlaced = allPlacedObjects.Where(p => p.EnableParentFormId is > 0).ToList();
        var disabledActors = disabledPlaced.Where(p => p.RecordType is "ACHR" or "ACRE").ToList();

        _output.WriteLine($"PlacedReference with IsInitiallyDisabled: {disabledPlaced.Count:N0} " +
                          $"(ACHR/ACRE: {disabledActors.Count:N0})");
        _output.WriteLine($"PlacedReference with EnableParentFormId: {enableParentPlaced.Count:N0}");

        Assert.True(disabledPlaced.Count > 0,
            "Some PlacedReference objects should have IsInitiallyDisabled after reconstruction");
        Assert.True(enableParentPlaced.Count > 0,
            "Some PlacedReference objects should have EnableParentFormId after reconstruction");

        // Step 6: Verify WastelandNV specifically
        var wasteland = collection.Worldspaces.FirstOrDefault(w => w.FormId == 0x000DA726);
        if (wasteland != null)
        {
            var wastelandObjects = wasteland.Cells
                .SelectMany(c => c.PlacedObjects)
                .ToList();
            var wastelandDisabled = wastelandObjects.Count(p => p.IsInitiallyDisabled);
            var wastelandEnableParent = wastelandObjects.Count(p => p.EnableParentFormId is > 0);
            var wastelandDisabledActors = wastelandObjects
                .Count(p => p.IsInitiallyDisabled && p.RecordType is "ACHR" or "ACRE");

            _output.WriteLine($"\nWastelandNV (0x000DA726):");
            _output.WriteLine($"  Total placed objects: {wastelandObjects.Count:N0}");
            _output.WriteLine($"  Initially Disabled: {wastelandDisabled:N0} " +
                              $"(actors: {wastelandDisabledActors:N0})");
            _output.WriteLine($"  With Enable Parent: {wastelandEnableParent:N0}");
        }
    }
}
