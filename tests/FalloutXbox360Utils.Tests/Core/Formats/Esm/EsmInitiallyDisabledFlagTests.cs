using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Tests verifying that the Initially Disabled flag (0x0800) and XESP (Enable Parent)
///     data flow through the full ESM analysis pipeline using synthetic ESM data.
/// </summary>
public class EsmInitiallyDisabledFlagTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void WorldspaceParsing_InitiallyDisabledFlag_ShouldBeTracked()
    {
        // Build synthetic ESM with a worldspace containing disabled refs and enable parents
        var enableParentRef = new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "REFR", FormId = 0x00100001, BaseFormId = 0x00050001,
            X = 100, Y = 200, Z = 0
        };

        var disabledAchr = new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "ACHR", FormId = 0x00100002, BaseFormId = 0x00060001,
            Flags = 0x0800, // Initially Disabled
            X = 110, Y = 210, Z = 0
        };

        var disabledWithEnableParent = new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "REFR", FormId = 0x00100003, BaseFormId = 0x00050002,
            Flags = 0x0800, // Initially Disabled
            EnableParentFormId = enableParentRef.FormId,
            EnableParentFlags = 0x01,
            X = 120, Y = 220, Z = 0
        };

        var disabledAcre = new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "ACRE", FormId = 0x00100004, BaseFormId = 0x00070001,
            Flags = 0x0800,
            X = 130, Y = 230, Z = 0
        };

        var normalRef = new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "REFR", FormId = 0x00100005, BaseFormId = 0x00050003,
            X = 140, Y = 240, Z = 0
        };

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
                PersistentRefs =
                [
                    enableParentRef,
                    disabledAchr,
                    disabledWithEnableParent,
                    disabledAcre,
                    normalRef
                ]
            }
        });

        var pipeline = builder.BuildAndAnalyze();
        var parsedRecords = pipeline.ParsedRecords;
        var scanResult = pipeline.ScanResult;
        var collection = pipeline.Collection;

        _output.WriteLine($"Parsed {parsedRecords.Count} records");

        // Step 1: Count records with Initially Disabled flag at raw level
        var disabledRecords = parsedRecords
            .Where(r => (r.Header.Flags & 0x0800) != 0)
            .ToList();
        _output.WriteLine($"Raw disabled records: {disabledRecords.Count}");
        Assert.True(disabledRecords.Count > 0,
            "Should find records with Initially Disabled flag (0x0800)");
        Assert.Contains(disabledRecords, r => r.Header.Signature == "ACHR");
        Assert.Contains(disabledRecords, r => r.Header.Signature == "ACRE");
        Assert.Contains(disabledRecords, r => r.Header.Signature == "REFR");

        // Step 2: Verify Initially Disabled on ExtractedRefrRecord
        var disabledRefrs = scanResult.RefrRecords
            .Where(r => r.Header.IsInitiallyDisabled)
            .ToList();
        _output.WriteLine($"ExtractedRefrRecords with IsInitiallyDisabled: {disabledRefrs.Count}");
        Assert.True(disabledRefrs.Count > 0);

        // Step 3: Verify XESP (Enable Parent) on ExtractedRefrRecord
        var withEnableParent = scanResult.RefrRecords
            .Where(r => r.EnableParentFormId is > 0)
            .ToList();
        _output.WriteLine($"ExtractedRefrRecords with EnableParentFormId: {withEnableParent.Count}");
        Assert.True(withEnableParent.Count > 0);

        // Step 4: Verify flags on PlacedReference after parsing
        var allPlacedObjects = collection.Worldspaces
            .SelectMany(w => w.Cells)
            .SelectMany(c => c.PlacedObjects)
            .ToList();
        _output.WriteLine($"Total PlacedReference objects: {allPlacedObjects.Count}");

        var disabledPlaced = allPlacedObjects.Where(p => p.IsInitiallyDisabled).ToList();
        var enableParentPlaced = allPlacedObjects.Where(p => p.EnableParentFormId is > 0).ToList();

        Assert.True(disabledPlaced.Count > 0,
            "Some PlacedReference objects should have IsInitiallyDisabled");
        Assert.True(enableParentPlaced.Count > 0,
            "Some PlacedReference objects should have EnableParentFormId");

        // Step 5: Verify WastelandNV specifically
        var wasteland = collection.Worldspaces.FirstOrDefault(w => w.FormId == 0x000DA726);
        Assert.NotNull(wasteland);
        _output.WriteLine($"WastelandNV cells: {wasteland.Cells.Count}");
    }
}