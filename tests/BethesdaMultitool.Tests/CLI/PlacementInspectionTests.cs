using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Tests.Helpers;
using Microsoft.VisualBasic.FileIO;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class PlacementInspectionTests
{
    private const uint CellId = 0x100;
    private const uint RefId = 0x200;
    private const uint BaseId = 0x300;

    [Theory]
    [InlineData("REFR")]
    [InlineData("ACHR")]
    [InlineData("ACRE")]
    public async Task BrowserCatalog_KeepsSearchablePhysicalCopies_AndReadsOnlySelectedPayload(string signature)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(temp, Fixture(signature)
            .AddRawChunk(CellGroup(CellId + 1, "OtherCell", Ref(signature, 9.75f))));
        using var source = await UnifiedAnalyzer.AnalyzeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var reads = 0;
        var browserSource = new PlacementBrowserSource(path, source.FileType, source.RawResult.EsmRecords!, (id, offset) =>
        {
            reads++;
            return PlacementQuery.Read(source, id, offset: offset);
        });
        var entries = PlacementBrowserCatalog.Create([browserSource]);
        Assert.Equal(0, reads);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => { Assert.Equal(RefId, e.FormId); Assert.Equal("PlacedAuditRef", e.EditorId); });
        Assert.NotEqual(entries[0].Offset, entries[1].Offset);
        var selected = Assert.IsType<PlacementOccurrence>(entries[1].Read());
        Assert.Equal(9.75f, selected.X);
        Assert.Equal(CellId + 1, selected.ParentCellFormId);
        Assert.Equal(Path.GetFullPath(path), selected.SourcePath);
        Assert.Same(selected, entries[1].Read());
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false, false, "Selected winner")]
    [InlineData(false, true, "Deleted winner")]
    [InlineData(true, false, "Ambiguous winning occurrences")]
    public void BrowserCatalog_SelectsSourceBeforeReading_AndRebasesLinks(bool duplicate, bool deleted, string status)
    {
        var paths = new[] { "Base.esm", "Other.esm", "Patch.esp" }.Select(Path.GetFullPath).ToArray();
        var order = PluginLoadOrder.Create(paths, false, p => p.EndsWith("Patch.esp", StringComparison.Ordinal) ? ["Base.esm"] : []);
        const uint ownId = 0x01000200;
        var baseHeader = new DetectedMainRecord("REFR", 0, 0, RefId, 10, false);
        var patchHeaders = new List<DetectedMainRecord>
        {
            baseHeader with { Offset = 20, Flags = deleted ? 0x20u : 0 },
            baseHeader with { FormId = ownId, Offset = 30 }
        };
        if (duplicate) { patchHeaders.Add(patchHeaders[0] with { Offset = 40 }); }
        var scans = new[] { new EsmRecordScanResult { MainRecords = [baseHeader] }, new EsmRecordScanResult(),
            new EsmRecordScanResult { MainRecords = patchHeaders } };
        var versions = scans.SelectMany((scan, i) => scan.MainRecords.Select(h => new LoadOrderRecordVersion(
            order.Entries[i].Name, paths[i], h.FormId, order.Map(paths[i], h.FormId).LoadOrderFormId,
            h.RecordType, null, h.Flags, h.Offset)));
        var index = LoadOrderRecordIndex.Create(order, versions);
        var selected = LoadOrderSelectionView.FromSources(order, index, []);
        var reads = new List<(string Path, long Offset)>();
        var sources = scans.Select((scan, i) => new PlacementBrowserSource(paths[i], AnalysisFileType.EsmFile, scan, (id, offset) =>
        {
            reads.Add((paths[i], offset));
            return [new PlacementOccurrence { SourcePath = paths[i], SourceKind = "plugin-record", RecordType = "REFR",
                FormId = id, Offset = offset, BaseFormId = 0x01000300, ParentCellFormId = 0x100,
                EnableParentFormId = 0x01000400, AssignmentSource = "physical-cell-child-GRUP", PayloadStatus = "readable" }];
        })).ToArray();
        var entries = PlacementBrowserCatalog.Create(sources, selected);
        Assert.Empty(reads);
        var overrides = entries.Where(e => e.FormId == RefId).ToArray();
        Assert.Equal(duplicate ? 2 : 1, overrides.Length);
        Assert.All(overrides, e => { Assert.Equal(paths[2], e.SourcePath); Assert.Equal(status, e.SelectionStatus); });
        var own = Assert.Single(entries.Where(e => e.FileLocalFormId == ownId));
        Assert.Equal(0x02000200u, own.FormId);
        var row = Assert.IsType<PlacementOccurrence>(own.Read());
        Assert.Equal(0x02000300u, row.BaseFormId);
        Assert.Equal(0x02000400u, row.EnableParentFormId);
        Assert.Equal(0x100u, row.ParentCellFormId);
        Assert.Equal((paths[2], 30L), Assert.Single(reads));
    }

    [Theory]
    [InlineData("REFR")]
    [InlineData("ACHR")]
    [InlineData("ACRE")]
    public async Task Show_ReportsPhysicalPlacementAndEnableParentWithoutClaimingRuntimeState(string signature)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(temp, Fixture(signature));
        using var source = await UnifiedAnalyzer.AnalyzeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        // Production loading releases raw REFR payloads; inspection must still recover the selected record.
        var row = Assert.Single(PlacementQuery.Read(source, RefId));
        Assert.Equal(signature, row.RecordType);
        Assert.Equal(CellId, row.ParentCellFormId);
        Assert.Equal(1.2345679f, row.X);
        Assert.Equal(-2.3456788f, row.RotZ);
        Assert.Equal(0x400u, row.EnableParentFormId);
        Assert.True(row.OppositeEnableParent);
        Assert.True(row.IsInitiallyDisabled);
        Assert.Equal("physical-cell-child-GRUP", row.AssignmentSource);
        Assert.True(row.Offset > 0);
        var (status, text) = Render(source, "PlacedAuditRef", row.Offset);
        Assert.Equal(0, status);
        Assert.Contains("1.2345679", text, StringComparison.Ordinal);
        Assert.Contains("-2.3456788", text, StringComparison.Ordinal);
        Assert.Contains("Runtime enabled state", text, StringComparison.Ordinal);
        Assert.Contains("Unavailable", text, StringComparison.Ordinal);
        Assert.Contains($"0x{row.Offset:X}", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIds_KeepBothOffsetsAndPhysicalCells_RequireExplicitSelection(bool duplicateCellId)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var secondCell = duplicateCellId ? CellId : CellId + 1;
        var builder = Fixture("REFR").AddRawChunk(CellGroup(secondCell, "AuditCellOther", Ref("REFR", 9.75f)));
        var path = CliExeRunner.WriteFalloutNvEsm(temp, builder);
        using var source = await UnifiedAnalyzer.AnalyzeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var rows = PlacementQuery.Read(source, RefId);
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].Offset, rows[1].Offset);
        Assert.Equal(new uint?[] { CellId, secondCell }, rows.Select(r => r.ParentCellFormId));
        Assert.NotEqual(rows[0].ParentCellOffset, rows[1].ParentCellOffset);
        Assert.Equal(2, Render(source, "0x200", null).Status);
        Assert.Equal(0, Render(source, "0x200", rows[1].Offset).Status);
        Assert.Equal(1, Render(source, "0x200", rows[1].Offset + 1).Status);
        var cells = PlacementQuery.ReadCells(source);
        Assert.Equal(2, PlacementQuery.ResolveCells(cells, "AuditCell").Count);
        var exactCell = Assert.Single(PlacementQuery.ResolveCells(cells, "AuditCellOne"));
        Assert.Equal(1.2345679f, Assert.Single(PlacementQuery.InCell(rows, exactCell, false, null)).X);
    }

    [Fact]
    public void PersistentOverlay_UsesHalfOpenGridBoundsAndKeepsDistinctOccurrencesOfDirectFormId()
    {
        var cell = new CellRecord { FormId = CellId, Offset = 10, GridX = -1, GridY = 0, WorldspaceFormId = 42 };
        var direct = new PlacementOccurrence { SourcePath = "fixture.esm", SourceKind = "plugin-record", RecordType = "REFR",
            FormId = RefId, Offset = 100, ParentCellFormId = CellId, ParentCellOffset = 10, WorldspaceFormId = 42,
            AssignmentSource = "physical-cell-child-GRUP", PayloadStatus = "readable", Flags = 0x400, X = -100, Y = 10 };
        var overlay = direct with { Offset = 200, ParentCellFormId = 999, ParentCellOffset = 20, X = -0.125f, Y = 4095.75f };
        PlacementOccurrence[] input = [direct, overlay, overlay with { Offset = 201, X = 0 },
            overlay with { Offset = 202, Y = 4096 }, overlay with { Offset = 203, WorldspaceFormId = 43 }];
        Assert.Single(PlacementQuery.InCell(input, cell, false, null));
        var rows = PlacementQuery.InCell(input, cell, true, "REFR");
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsPersistentOverlay);
        Assert.True(rows[1].IsPersistentOverlay);
        var report = CellObjectsOutput.Create("fixture.esm", "0x100", cell, true, "REFR", 1, rows);
        Assert.Equal(2, report.TotalCount);
        Assert.Equal(1, report.ReturnedCount);
        Assert.True(report.Truncated);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task StructuredOutput_IsStandaloneAndRoundTripsPrecisionAndQuotedNames(string format)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(temp, Fixture("REFR", "Placed,\"Audit\"\nRef"));
        var result = await CliExeRunner.RunAsync(["esm", "cell", "objects", path, "AuditCellOne", "--format", format, "--limit", "0"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Invalid FormID", result.StandardError, StringComparison.OrdinalIgnoreCase);
        if (format == "json")
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
            var row = Assert.Single(doc.RootElement.GetProperty("objects").EnumerateArray());
            Assert.Equal(1.2345679f, row.GetProperty("x").GetSingle());
            Assert.Equal(-2.3456788f, row.GetProperty("rotZ").GetSingle());
            Assert.Equal("Placed,\"Audit\"\nRef", row.GetProperty("editorId").GetString());
            Assert.Equal(Path.GetFullPath(path), row.GetProperty("sourcePath").GetString());
            Assert.True(row.GetProperty("offset").GetInt64() > 0);
        }
        else
        {
            using var parser = new TextFieldParser(new StringReader(result.StandardOutput)) { HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var header = parser.ReadFields()!;
            var values = parser.ReadFields()!;
            Assert.Equal(header.Length, values.Length);
            var row = header.Zip(values).ToDictionary(p => p.First, p => p.Second);
            Assert.Equal("Placed,\"Audit\"\nRef", row["EditorId"].Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal(1.2345679f, float.Parse(row["X"], CultureInfo.InvariantCulture));
            Assert.Equal(-2.3456788f, float.Parse(row["RotZ"], CultureInfo.InvariantCulture));
            Assert.Equal(Path.GetFullPath(path), row["SourcePath"]);
            Assert.True(parser.EndOfData);
        }
    }

    [Theory]
    [InlineData("0x00000100", 0)]
    [InlineData("AuditCellOne", 0)]
    [InlineData("AuditCell", 2)]
    [InlineData("MissingCell", 1)]
    public async Task CellLookup_ReturnsMeaningfulStatusForEverySupportedIdentifier(string query, int expected)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(temp, Fixture("REFR")
            .AddRawChunk(CellGroup(CellId + 1, "AuditCellOther", Ref("REFR", 9.75f))));
        var output = Path.Combine(temp.Path, "placements.json");
        var result = await CliExeRunner.RunAsync(["esm", "cell", "objects", path, query, "--format", "json", "--output", output],
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.DoesNotContain("Invalid FormID", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected == 0, File.Exists(output));
    }

    [Fact]
    public async Task OutputFile_RefusesOverwrite_AndDeletedReferenceHasUnknownCoordinates()
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(temp, new EsmTestFileBuilder()
            .AddRawChunk(CellGroup(CellId, "AuditCellOne", EsmTestFileBuilder.BuildRecord("REFR", RefId, 0x20))));
        using var source = await UnifiedAnalyzer.AnalyzeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var row = Assert.Single(PlacementQuery.Read(source, RefId));
        Assert.True(row.IsDeleted);
        Assert.Null(row.X);
        Assert.Null(row.BaseFormId);
        Assert.Equal(0, Render(source, "0x200", null).Status);
        var output = Path.Combine(temp.Path, "existing.csv");
        await File.WriteAllTextAsync(output, "keep me", TestContext.Current.CancellationToken);
        Assert.Equal(1, await EsmCellCommand.RunObjectsAsync(path, "AuditCellOne", false, null, 0, "csv", output, null,
            TestContext.Current.CancellationToken));
        Assert.Equal("keep me", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    private static (int? Status, string Text) Render(UnifiedAnalysisResult source, string query, long? offset)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(text), Ansi = AnsiSupport.No });
        console.Profile.Width = 300;
        var status = ShowCommand.RenderPlacementLookup(source, query, offset, new ShowRenderContext(console, true, false));
        return (status, text.ToString());
    }

    [Theory]
    [InlineData("retained", 1, 0)]
    [InlineData("replaced", 1, 0)]
    [InlineData("moved", 1, 0)]
    [InlineData("deleted", 0, 1)]
    [InlineData("unparsed", 0, 1)]
    [InlineData("duplicate", 0, 2)]
    public async Task CellObjects_LoadOrder_UsesIndependentPhysicalWinners(string variant, int count, int issues)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var original = CliExeRunner.WriteFalloutNvEsm(temp, Fixture("REFR"));
        byte[] child = variant switch
        {
            "retained" => [],
            "deleted" => EsmTestFileBuilder.BuildRecord("REFR", RefId, 0x20),
            "unparsed" => EsmTestFileBuilder.BuildRecord("REFR", RefId, 0),
            "duplicate" => [.. Ref("REFR", 9.75f), .. Ref("REFR", 8.125f)],
            _ => Ref("REFR", 9.75f)
        };
        var parent = variant == "moved" ? CellId + 1 : CellId;
        var patch = CliExeRunner.WritePlugin(temp, "Patch.esp", new EsmTestFileBuilder().WithMasters("FalloutNV.esm")
            .AddRawChunk(CellGroup(parent, "WinningCell", child)));
        var output = Path.Combine(temp.Path, "merged.json");
        var result = await CliExeRunner.RunAsync(["esm", "cell", "objects", patch, "WinningCell", "--load-order",
            original + ";" + patch, "--format", "json", "--output", output, "--limit", "0"], TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Empty(result.StandardOutput);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        var report = document.RootElement;
        Assert.Equal(2, report.GetProperty("loadOrder").GetArrayLength());
        Assert.Equal(count, report.GetProperty("objects").GetArrayLength());
        Assert.Equal(issues, report.GetProperty("selectionIssues").GetArrayLength());
        Assert.Equal(Path.GetFullPath(patch), report.GetProperty("cellSourcePath").GetString());
        Assert.Equal(parent, report.GetProperty("cellFormId").GetUInt32());
        if (count > 0)
        {
            var row = report.GetProperty("objects")[0];
            Assert.Equal(variant == "retained" ? 1.2345679f : 9.75f, row.GetProperty("x").GetSingle());
            Assert.Equal(variant == "retained" ? original : patch, row.GetProperty("sourcePath").GetString());
            Assert.Equal(RefId, row.GetProperty("fileLocalFormId").GetUInt32());
            Assert.Equal(parent, row.GetProperty("parentCellFormId").GetUInt32());
            Assert.Equal("included-in-typed-view", row.GetProperty("selectionStatus").GetString());
        }
        else
        {
            var excluded = report.GetProperty("selectionIssues");
            var expectedStatus = variant switch { "deleted" => "excluded-deleted", "unparsed" => "not-in-typed-view", _ => "excluded-ambiguous-winner" };
            Assert.All(excluded.EnumerateArray(), row => Assert.Equal(expectedStatus,
                row.GetProperty("selectionStatus").GetString()));
            if (variant != "duplicate") Assert.Equal(JsonValueKind.Null, excluded[0].GetProperty("x").ValueKind);
            else Assert.NotEqual(excluded[0].GetProperty("offset").GetInt64(), excluded[1].GetProperty("offset").GetInt64());
        }
        var csv = Path.Combine(temp.Path, "merged.csv");
        Assert.Equal(0, await EsmCellCommand.RunObjectsAsync(patch, "WinningCell", false, null, 0, "csv", csv, null,
            TestContext.Current.CancellationToken, [original, patch]));
        using var parser = new TextFieldParser(csv) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var columns = parser.ReadFields()!;
        var csvRows = 0;
        while (!parser.EndOfData) { Assert.Equal(columns.Length, parser.ReadFields()!.Length); ++csvRows; }
        Assert.Equal(count + issues, csvRows);
    }

    [Fact]
    public void SelectedPlacementNamespace_KeepsOriginalIdsAndPhysicalParentOffset()
    {
        var paths = new[] { "FalloutNV.esm", "Earlier.esm", "Patch.esp" }.Select(Path.GetFullPath).ToArray();
        var order = PluginLoadOrder.Create(paths, false, path => path == paths[0] ? [] : ["FalloutNV.esm"]);
        var physical = new PlacementOccurrence { SourcePath = paths[2], SourceKind = "plugin-record", RecordType = "REFR",
            FormId = 0x01000900, BaseFormId = 0x01000A00, ParentCellFormId = 0x01000800, ParentCellOffset = 24,
            WorldspaceFormId = 0x01000B00, EnableParentFormId = 0x01000C00, Offset = 80,
            AssignmentSource = "physical-cell-child-GRUP", PayloadStatus = "readable" };
        var index = LoadOrderRecordIndex.Create(order, [new("Patch.esp", paths[2], physical.FormId, 0x02000900, "REFR", null, 0, 80)]);
        var view = LoadOrderSelectionView.FromSources(order, index, []);
        var row = Assert.Single(LoadOrderPlacementQuery.WinningOccurrences(view, order.Entries[2], [physical]));
        Assert.Equal(0x02000900u, row.FormId);
        Assert.Equal(0x02000A00u, row.BaseFormId);
        Assert.Equal(0x02000800u, row.ParentCellFormId);
        Assert.Equal(0x02000B00u, row.WorldspaceFormId);
        Assert.Equal(0x02000C00u, row.EnableParentFormId);
        Assert.Equal(physical.FormId, row.FileLocalFormId);
        Assert.Equal(physical.ParentCellFormId, row.FileLocalParentCellFormId);
        Assert.Equal(24, row.ParentCellOffset);
    }

    private static EsmTestFileBuilder Fixture(string signature, string editorId = "PlacedAuditRef") => new EsmTestFileBuilder()
        .AddTopLevelGrup("STAT", EsmTestFileBuilder.BuildRecord("STAT", BaseId, 0, ("EDID", Encoding.UTF8.GetBytes("BaseObject\0"))))
        .AddRawChunk(CellGroup(CellId, "AuditCellOne", Ref(signature, 1.2345679f, editorId)));

    private static byte[] Ref(string signature, float x, string editorId = "PlacedAuditRef")
    {
        var coordinates = new byte[24];
        float[] values = [x, -22.123456f, 3.25f, 0.25f, 1.5f, -2.3456788f];
        for (var i = 0; i < values.Length; i++) { BinaryPrimitives.WriteSingleLittleEndian(coordinates.AsSpan(i * 4), values[i]); }
        return EsmTestFileBuilder.BuildRecord(signature, RefId, 0xC00, ("NAME", BitConverter.GetBytes(BaseId)),
            ("EDID", Encoding.UTF8.GetBytes(editorId + '\0')), ("DATA", coordinates), ("XESP", new byte[] { 0, 4, 0, 0, 1, 0, 0, 0 }));
    }

    private static byte[] CellGroup(uint cellId, string name, byte[] placement) => Group(0, 0,
        EsmTestFileBuilder.BuildRecord("CELL", cellId, 0, ("EDID", Encoding.UTF8.GetBytes(name + '\0')), ("DATA", new byte[] { 1 })),
        Group(6, cellId, Group(8, cellId, placement)));

    private static byte[] Group(int type, uint label, params byte[][] children)
    {
        var data = new byte[24 + children.Sum(c => c.Length)];
        Encoding.ASCII.GetBytes("GRUP", data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length);
        if (type == 0) { Encoding.ASCII.GetBytes("CELL", data.AsSpan(8)); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), label); }
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), type);
        var offset = 24;
        foreach (var child in children) { child.CopyTo(data, offset); offset += child.Length; }
        return data;
    }
}
