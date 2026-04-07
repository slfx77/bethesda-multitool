using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Presentation;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Core.VersionTracking.Extraction;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using FalloutXbox360Utils.Tests.Helpers;
using FalloutXbox360Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Semantic;

public sealed class SemanticConsolidationTests(SampleFileFixture samples) : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    [Fact]
    public async Task SemanticFileLoader_load_async_matches_analyze_then_load_for_synthetic_esm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var filePath = WriteSyntheticEsm(
            "synthetic.esm",
            EsmTestFileBuilder.BuildRecord(
                "STAT",
                0x00001000,
                0,
                ("EDID", NullTerm("StatBase")),
                ("FULL", NullTerm("Base Stat"))));

        using var loaded = await SemanticFileLoader.LoadAsync(
            filePath,
            new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
            cancellationToken);

        var analysis = await SemanticFileLoader.AnalyzeOnlyAsync(
            filePath,
            new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
            cancellationToken);

        using var loadedFromAnalysis =
            SemanticFileLoader.LoadFromAnalysisResult(filePath, analysis, AnalysisFileType.EsmFile);

        Assert.Equal(loadedFromAnalysis.Records.TotalRecordsParsed, loaded.Records.TotalRecordsParsed);
        Assert.Equal("StatBase", loaded.Resolver.GetEditorId(0x00001000));
        Assert.Equal(loadedFromAnalysis.Resolver.GetEditorId(0x00001000), loaded.Resolver.GetEditorId(0x00001000));
        Assert.Equal(loadedFromAnalysis.Resolver.GetDisplayName(0x00001000), loaded.Resolver.GetDisplayName(0x00001000));
    }

    [Fact]
    public async Task SemanticSourceSetBuilder_merges_sources_in_load_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var basePath = WriteSyntheticEsm(
            "base.esm",
            EsmTestFileBuilder.BuildRecord(
                "STAT",
                0x00002000,
                0,
                ("EDID", NullTerm("StatBase")),
                ("FULL", NullTerm("Base Name"))));

        var overlayPath = WriteSyntheticEsm(
            "overlay.esm",
            EsmTestFileBuilder.BuildRecord(
                "STAT",
                0x00002000,
                0,
                ("EDID", NullTerm("StatOverlay")),
                ("FULL", NullTerm("Overlay Name"))));

        var sourceSet = await SemanticSourceSetBuilder.LoadSourcesAsync(
        [
            new SemanticSourceRequest { FilePath = basePath, FileType = AnalysisFileType.EsmFile },
            new SemanticSourceRequest { FilePath = overlayPath, FileType = AnalysisFileType.EsmFile }
        ],
            cancellationToken: cancellationToken);

        var mergedResolver = sourceSet.BuildMergedResolver();
        var mergedRecords = sourceSet.BuildMergedRecords();

        Assert.NotNull(mergedResolver);
        Assert.NotNull(mergedRecords);
        Assert.Equal("StatOverlay", mergedResolver!.GetEditorId(0x00002000));
        Assert.Equal("Overlay Name", mergedResolver.GetDisplayName(0x00002000));
    }

    [Fact]
    public async Task SemanticFileLoader_loads_dump_fixture_when_available()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug dump sample not available");
        var cancellationToken = TestContext.Current.CancellationToken;

        using var loaded = await SemanticFileLoader.LoadAsync(
            samples.DebugDump!,
            new SemanticFileLoadOptions
            {
                FileType = AnalysisFileType.Minidump,
                IncludeMetadata = true
            },
            cancellationToken);

        Assert.Equal(AnalysisFileType.Minidump, loaded.FileType);
        Assert.True(loaded.Records.TotalRecordsParsed > 0);
        Assert.NotNull(loaded.RawResult.MinidumpInfo);
    }

    [Fact]
    public async Task VersionSnapshotPipeline_wrapper_matches_shared_pipeline_for_synthetic_esm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var filePath = WriteSyntheticEsm(
            "snapshot.esm",
            EsmTestFileBuilder.BuildRecord(
                "STAT",
                0x00003000,
                0,
                ("EDID", NullTerm("SnapshotStat")),
                ("FULL", NullTerm("Snapshot Stat"))));

        var buildInfo = new BuildInfo
        {
            Label = "Synthetic",
            SourcePath = filePath,
            SourceType = BuildSourceType.Esm
        };

        var wrapperSnapshot = await EsmSnapshotExtractor.ExtractAsync(filePath, buildInfo, cancellationToken: cancellationToken);
        var pipelineSnapshot = await VersionSnapshotPipeline.ExtractAsync(
            filePath,
            buildInfo,
            new VersionSnapshotPipelineOptions
            {
                FileType = AnalysisFileType.EsmFile,
                AnalysisPhaseLabel = "Analyzing ESM file..."
            },
            cancellationToken: cancellationToken);

        Assert.Equal(wrapperSnapshot.TotalRecordCount, pipelineSnapshot.TotalRecordCount);
        Assert.Equal(wrapperSnapshot.Weapons.Count, pipelineSnapshot.Weapons.Count);
        Assert.Equal(wrapperSnapshot.Quests.Count, pipelineSnapshot.Quests.Count);
    }

    [Fact]
    public async Task CrossDumpOutputWriter_writes_html_from_structured_records()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDir = CreateTempDirectory();
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Weapon"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00004000] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Weapon",
                    0x00004000,
                    "WeapTest",
                    "Test Weapon",
                    [new ReportSection("Stats", [new ReportField("Damage", ReportValue.Int(12))])])
            }
        };

        var writtenFiles = await CrossDumpOutputWriter.WriteAsync(index, outputDir, "html", cancellationToken);

        Assert.Contains(Path.Combine(outputDir, "index.html"), writtenFiles);
        Assert.Contains(Path.Combine(outputDir, "compare_weapon.html"), writtenFiles);
        Assert.Contains("Cross-Build Comparison", await File.ReadAllTextAsync(Path.Combine(outputDir, "compare_weapon.html")));
    }

    [Fact]
    public async Task CrossDumpOutputWriter_writes_cell_html_with_valid_embedded_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDir = CreateTempDirectory();
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Cell",
                    0x00005000,
                    "GoodspringsCell",
                    "Goodsprings",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            }
        };
        index.CellGridCoords[0x00005000] = (4, -2);

        await CrossDumpOutputWriter.WriteAsync(index, outputDir, "html", cancellationToken);

        var html = await File.ReadAllTextAsync(Path.Combine(outputDir, "compare_cell.html"), cancellationToken);
        var payload = ExtractCompressedPayload(html);
        var json = InflateBase64(payload);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Cell", document.RootElement.GetProperty("recordType").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("gridCoords").GetProperty("0x00005000")[0].GetInt32());
        Assert.Equal(-2, document.RootElement.GetProperty("gridCoords").GetProperty("0x00005000")[1].GetInt32());
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_splits_oversized_cell_pages_into_subpages()
    {
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Cell",
                    0x00005000,
                    "GoodspringsA",
                    "Goodsprings A",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            },
            [0x00005001] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Cell",
                    0x00005001,
                    "GoodspringsB",
                    "Goodsprings B",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            }
        };
        index.RecordGroups["Cell"] = new Dictionary<uint, string>
        {
            [0x00005000] = "Goodsprings",
            [0x00005001] = "Goodsprings"
        };
        index.CellGridCoords[0x00005000] = (4, -2);
        index.CellGridCoords[0x00005001] = (5, -2);

        var files = CrossDumpJsonHtmlWriter.GenerateAll(index, maxInlineCompressedPayloadLength: 1);

        Assert.Contains("compare_cell.html", files.Keys);
        Assert.Contains(files.Keys, key => key.StartsWith("compare_cell_", StringComparison.Ordinal) && key != "compare_cell.html");
        Assert.Contains("split into smaller pages", files["compare_cell.html"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_splits_cell_pages_when_raw_json_payload_is_large()
    {
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Cell",
                    0x00005000,
                    "GoodspringsA",
                    "Goodsprings A",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            },
            [0x00005001] = new Dictionary<int, RecordReport>
            {
                [0] = new(
                    "Cell",
                    0x00005001,
                    "GoodspringsB",
                    "Goodsprings B",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            }
        };
        index.RecordGroups["Cell"] = new Dictionary<uint, string>
        {
            [0x00005000] = "Goodsprings",
            [0x00005001] = "Goodsprings"
        };

        var files = CrossDumpJsonHtmlWriter.GenerateAll(
            index,
            maxInlineCompressedPayloadLength: int.MaxValue,
            maxCellJsonPayloadLength: 1);

        Assert.Contains("compare_cell.html", files.Keys);
        Assert.Contains(files.Keys, key => key.StartsWith("compare_cell_", StringComparison.Ordinal) && key != "compare_cell.html");
    }

    [Fact]
    public void ComparisonJsRenderer_uses_streaming_json_parse_for_embedded_payload()
    {
        Assert.Contains("function createInflatedReadable(bytes, format)", ComparisonJsRenderer.Script);
        Assert.Contains("new Blob([bytes]).stream().pipeThrough(new DecompressionStream(format))", ComparisonJsRenderer.Script);
        Assert.Contains("return new Response(createInflatedReadable(bytes, format)).json();", ComparisonJsRenderer.Script);
        Assert.Contains("setLoadingStatus('Inflating comparison data (' + format + ')...');", ComparisonJsRenderer.Script);
        Assert.Contains("var formats = ['deflate', 'deflate-raw'];", ComparisonJsRenderer.Script);
        Assert.Contains("function readCompressedPayload()", ComparisonJsRenderer.Script);
        Assert.Contains("window.__comparisonDebug = DEBUG_STATE;", ComparisonJsRenderer.Script);
        Assert.Contains("DEBUG_STATE.summary = summary;", ComparisonJsRenderer.Script);
        Assert.Contains("function renderFailureDetails(summary)", ComparisonJsRenderer.Script);
        Assert.Contains("comparison-debug-output", ComparisonJsRenderer.Script);
        Assert.Contains("console.error('[comparison] load failed'", ComparisonJsRenderer.Script);
        Assert.Contains("querySelectorAll('.record-data-chunk')", ComparisonJsRenderer.Script);
        Assert.Contains("function renderPlacedObjectInline(val)", ComparisonJsRenderer.Script);
        Assert.Contains("function looksLikePlacedObjectComposite(val)", ComparisonJsRenderer.Script);
        Assert.Contains("compositeFieldText(fieldMap, 'Position')", ComparisonJsRenderer.Script);
        Assert.Contains("compositeFieldText(fieldMap, 'Rotation')", ComparisonJsRenderer.Script);
        Assert.Contains("rd-list-meta", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("JSON.parse(json)", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("await w.write(bytes);", ComparisonJsRenderer.Script);
        Assert.Contains("new TextDecoder().decode(bytes);", ComparisonJsRenderer.Script);
    }

    [Fact]
    public void GeckWorldWriter_builds_cell_report_with_placed_object_position_rotation_and_disabled_fields()
    {
        var resolver = new FormIdResolver([], [], []);
        var cell = new CellRecord
        {
            FormId = 0x00006000,
            EditorId = "PlacedObjectCell",
            FullName = "Placed Object Test Cell",
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0x00007000,
                    BaseFormId = 0x00008000,
                    BaseEditorId = "TestCrate",
                    RecordType = "REFR",
                    X = 1.0f,
                    Y = 2.0f,
                    Z = 3.0f,
                    RotZ = 0.5f,
                    IsInitiallyDisabled = true
                }
            ]
        };

        var report = GeckWorldWriter.BuildCellReport(cell, resolver);
        var placedObjects = Assert.Single(report.Sections.Where(section => section.Name == "Placed Objects"));
        var objectsField = Assert.Single(placedObjects.Fields.Where(field => field.Key == "Objects"));
        var objectList = Assert.IsType<ReportValue.ListVal>(objectsField.Value);
        var objectItem = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(objectList.Items));
        var fields = objectItem.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal("(1.0, 2.0, 3.0)", Assert.IsType<ReportValue.StringVal>(fields["Position"]).Raw);
        Assert.Equal("(0.000, 0.000, 0.500)", Assert.IsType<ReportValue.StringVal>(fields["Rotation"]).Raw);
        Assert.True(Assert.IsType<ReportValue.BoolVal>(fields["Disabled"]).Raw);
    }

    [Fact]
    public void RecordDetailPresenter_and_app_adapter_preserve_order_and_link_targets()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x10] = "RaceHuman",
                [0x20] = "Ammo556",
                [0x30] = "QuestScript",
                [0x40] = "WeapRifle",
                [0x50] = "WastelandNV"
            },
            new Dictionary<uint, string>
            {
                [0x10] = "Human",
                [0x20] = "5.56mm Round",
                [0x30] = "Quest Script",
                [0x40] = "Service Rifle",
                [0x50] = "Mojave Wasteland"
            },
            []);

        var cases = new (object Record, string ExpectedLinkLabel, uint ExpectedLinkedFormId)[]
        {
            (new NpcRecord { FormId = 1, EditorId = "NpcDoc", FullName = "Doc", Race = 0x10 }, "Race", 0x10),
            (new WeaponRecord { FormId = 2, EditorId = "WeapRifle", FullName = "Rifle", AmmoFormId = 0x20 }, "Ammo", 0x20),
            (new QuestRecord { FormId = 3, EditorId = "QuestMain", FullName = "Main Quest", Script = 0x30 }, "Script", 0x30),
            (new PackageRecord
            {
                FormId = 4,
                EditorId = "PackGuard",
                Data = new PackageData(),
                UseWeaponData = new PackageUseWeaponData { WeaponFormId = 0x40 }
            }, "Weapon", 0x40),
            (new CellRecord { FormId = 5, EditorId = "GoodspringsCell", FullName = "Goodsprings", WorldspaceFormId = 0x50 }, "Worldspace", 0x50)
        };

        foreach (var testCase in cases)
        {
            Assert.True(
                RecordDetailPresenter.TryBuildForRecord(testCase.Record, null, resolver, out var model) &&
                model != null);

            var expectedLabels = model.Sections
                .SelectMany(section => section.Entries)
                .Select(entry => entry.Label)
                .ToList();
            var properties = RecordDetailPropertyAdapter.Convert(model);

            Assert.Equal(expectedLabels, properties.Select(property => property.Name).ToList());
            Assert.Equal(
                testCase.ExpectedLinkedFormId,
                properties.Single(property => property.Name == testCase.ExpectedLinkLabel).LinkedFormId);
        }
    }

    [Fact]
    public void Structural_guard_limits_manual_semantic_parser_sites_to_allowlist()
    {
        var repoRoot = FindRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src", "FalloutXbox360Utils");
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("src", "FalloutXbox360Utils", "Core", "Semantic", "SemanticFileLoader.cs"),
            Path.Combine("src", "FalloutXbox360Utils", "CLI", "Commands", "Analysis", "AnalyzeCommand.cs"),
            Path.Combine("src", "FalloutXbox360Utils", "CLI", "Commands", "Dialogue", "DialogueProvenanceCommand.cs"),
            Path.Combine("src", "FalloutXbox360Utils", "CLI", "Commands", "Esm", "EsmCellCommand.cs"),
            Path.Combine("src", "FalloutXbox360Utils", "CLI", "Commands", "Esm", "EsmCommand.cs"),
            Path.Combine("src", "FalloutXbox360Utils", "Core", "Minidump", "MinidumpExtractionReporter.cs")
        };

        var offenders = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("MemoryMappedFile.CreateFromFile", StringComparison.Ordinal) &&
                       text.Contains("new RecordParser(", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .Where(path => !allowedFiles.Contains(path))
            .OrderBy(path => path)
            .ToList();

        Assert.True(offenders.Count == 0, "Unexpected manual semantic parse sites:\n" + string.Join("\n", offenders));
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp test artifacts.
            }
        }
    }

    private string WriteSyntheticEsm(string fileName, params byte[][] records)
    {
        var builder = new EsmTestFileBuilder();
        builder.AddTopLevelGrup("STAT", records);

        var directory = CreateTempDirectory();
        var filePath = Path.Combine(directory, fileName);
        File.WriteAllBytes(filePath, builder.Build());
        return filePath;
    }

    private string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "semantic-consolidation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    private static byte[] NullTerm(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }

    private static string ExtractCompressedPayload(string html)
    {
        var attributeMatch = Regex.Match(html, "data-z=\"(?<payload>[^\"]+)\"");
        if (attributeMatch.Success)
        {
            return attributeMatch.Groups["payload"].Value;
        }

        var chunkMatches = Regex.Matches(
            html,
            "<script type=\"application/octet-stream\" class=\"record-data-chunk\">(?<chunk>.*?)</script>",
            RegexOptions.Singleline);
        if (chunkMatches.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (Match match in chunkMatches)
            {
                builder.Append(match.Groups["chunk"].Value.Trim());
            }

            return builder.ToString();
        }

        var singleScriptMatch = Regex.Match(
            html,
            "<script type=\"application/octet-stream\" id=\"record-data\">(?<payload>.*?)</script>",
            RegexOptions.Singleline);
        Assert.True(singleScriptMatch.Success, "Expected a compressed comparison payload in the generated HTML.");
        return singleScriptMatch.Groups["payload"].Value.Trim();
    }

    private static string InflateBase64(string payload)
    {
        var bytes = Convert.FromBase64String(payload);
        using var input = new MemoryStream(bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
