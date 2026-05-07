using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Presentation;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Core.VersionTracking.Extraction;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Semantic;

public sealed class SemanticConsolidationTests(SampleFileFixture samples) : IDisposable
{
    private readonly List<string> _tempDirectories = [];

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
        Assert.Equal(loadedFromAnalysis.Resolver.GetDisplayName(0x00001000),
            loaded.Resolver.GetDisplayName(0x00001000));
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

        var wrapperSnapshot =
            await EsmSnapshotExtractor.ExtractAsync(filePath, buildInfo, cancellationToken: cancellationToken);
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
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Weapon"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00004000] = new()
            {
                [0] = new RecordReport(
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
        Assert.Contains("Cross-Build Comparison",
            await File.ReadAllTextAsync(Path.Combine(outputDir, "compare_weapon.html"), cancellationToken));
    }

    [Fact]
    public async Task CrossDumpHtmlWriter_writes_single_record_type_and_summary_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDir = CreateTempDirectory();
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["NPC"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00006000] = new()
            {
                [0] = new RecordReport(
                    "NPC",
                    0x00006000,
                    "NpcTest",
                    "Test NPC",
                    [new ReportSection("Identity", [new ReportField("Level", ReportValue.Int(4))])])
            }
        };
        index.StructuredRecords["Weapon"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00007000] = new()
            {
                [0] = new RecordReport(
                    "Weapon",
                    0x00007000,
                    "WeapTest",
                    "Test Weapon",
                    [new ReportSection("Stats", [new ReportField("Damage", ReportValue.Int(12))])])
            }
        };

        var npcFile = await CrossDumpHtmlWriter.WriteRecordTypeFileAsync(
            index,
            "NPC",
            outputDir,
            cancellationToken);
        var npcSummary = CrossDumpJsonHtmlWriter.BuildRecordTypeSummary(
            "NPC",
            index.StructuredRecords["NPC"],
            index.Dumps.Count);
        var indexFile = await CrossDumpHtmlWriter.WriteIndexPageAsync(
            index.Dumps,
            [npcSummary],
            outputDir,
            cancellationToken);

        Assert.Equal(Path.Combine(outputDir, "compare_npc.html"), npcFile);
        Assert.True(File.Exists(npcFile));
        Assert.False(File.Exists(Path.Combine(outputDir, "compare_weapon.html")));

        var indexHtml = await File.ReadAllTextAsync(indexFile, cancellationToken);
        Assert.Contains("compare_npc.html", indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("compare_weapon.html", indexHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossDumpOutputWriter_writes_cell_html_with_valid_embedded_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDir = CreateTempDirectory();
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new()
            {
                [0] = new RecordReport(
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
    public void EsmNpcParser_reads_physical_characteristics_from_nam6_nam7_and_hclr()
    {
        var record = EsmTestFileBuilder.BuildRecord(
            "NPC_",
            0x00006000,
            0,
            ("EDID", NullTerm("NpcPhysical")),
            ("FULL", NullTerm("Physical NPC")),
            ("HNAM", UInt32Le(0x00006100)),
            ("LNAM", FloatLe(0.65f)),
            ("ENAM", UInt32Le(0x00006200)),
            ("HCLR", [0x11, 0x22, 0x33, 0x00]),
            ("NAM6", FloatLe(1.08f)),
            ("NAM7", FloatLe(72.5f)));

        var pipeline = new EsmTestFileBuilder()
            .AddTopLevelGrup("NPC_", record)
            .BuildAndAnalyze();

        var npc = Assert.Single(pipeline.Collection.Npcs);
        Assert.Equal(0x00006100u, npc.HairFormId);
        Assert.Equal(0.65f, npc.HairLength);
        Assert.Equal(0x00006200u, npc.EyesFormId);
        Assert.Equal("#112233 (17, 34, 51)", NpcRecord.FormatHairColor(npc.HairColor));
        Assert.Equal(1.08f, npc.Height);
        Assert.Equal(72.5f, npc.Weight);

        var report = GeckActorDetailWriter.BuildNpcReport(npc, pipeline.Collection.CreateResolver());
        var physicalFields = Assert.Single(report.Sections, section => section.Name == "Physical Traits")
            .Fields
            .Select(field => field.Key)
            .ToArray();

        Assert.Contains("Hairstyle", physicalFields);
        Assert.Contains("Hair Length", physicalFields);
        Assert.Contains("Hair Color", physicalFields);
        Assert.Contains("Eyes", physicalFields);
        Assert.Contains("Height", physicalFields);
        Assert.Contains("Weight", physicalFields);
    }

    [Fact]
    public void EsmNpcParser_treats_zero_height_and_weight_as_default_scale()
    {
        var record = EsmTestFileBuilder.BuildRecord(
            "NPC_",
            0x00006001,
            0,
            ("EDID", NullTerm("NpcDefaultScale")),
            ("NAM6", FloatLe(0f)),
            ("NAM7", FloatLe(0f)));

        var pipeline = new EsmTestFileBuilder()
            .AddTopLevelGrup("NPC_", record)
            .BuildAndAnalyze();

        var npc = Assert.Single(pipeline.Collection.Npcs);
        Assert.Equal(1.0f, npc.Height);
        Assert.Equal(1.0f, npc.Weight);
    }

    [Fact]
    public void Npc_factions_use_stable_section_and_form_id_identity_for_diffing()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [0x00007100] = "GoodspringsFaction" },
            new Dictionary<uint, string> { [0x00007100] = "Goodsprings" },
            []);
        var report = GeckActorDetailWriter.BuildNpcReport(
            new NpcRecord
            {
                FormId = 0x00007000,
                EditorId = "NpcFactionMember",
                Factions = [new FactionMembership(0x00007100, 2)]
            },
            resolver);

        var section = Assert.Single(report.Sections, section => section.Name == "Factions");
        Assert.DoesNotContain(report.Sections,
            section => section.Name.StartsWith("Factions (", StringComparison.Ordinal));
        Assert.DoesNotContain(section.Fields, field => field.Key == "Count");

        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "Factions").Value);
        Assert.Equal("1 factions", list.Display);
        var item = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        var fields = item.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(0x00007100u, Assert.IsType<ReportValue.FormIdVal>(fields["Faction"]).Raw);
        Assert.Equal(2, Assert.IsType<ReportValue.IntVal>(fields["Rank"]).Raw);
    }

    [Fact]
    public void Npc_inventory_uses_stable_section_and_item_form_id_identity_for_diffing()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [0x00007100] = "NVFancyLadsSnackCakes" },
            new Dictionary<uint, string> { [0x00007100] = "Fancy Lads Snack Cakes" },
            []);
        var report = GeckActorDetailWriter.BuildNpcReport(
            new NpcRecord
            {
                FormId = 0x00007000,
                EditorId = "MannyVargas",
                Inventory = [new InventoryItem(0x00007100, 2)]
            },
            resolver);

        var section = Assert.Single(report.Sections, section => section.Name == "Inventory");
        Assert.DoesNotContain(report.Sections,
            section => section.Name.StartsWith("Inventory (", StringComparison.Ordinal));

        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "Items").Value);
        Assert.Equal("1 items", list.Display);
        var item = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        var fields = item.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(0x00007100u, Assert.IsType<ReportValue.FormIdVal>(fields["Item"]).Raw);
        Assert.Equal(2, Assert.IsType<ReportValue.IntVal>(fields["Qty"]).Raw);
    }

    [Fact]
    public void Script_variables_use_stable_section_name_for_comparison_alignment()
    {
        var report = GeckScriptWriter.BuildScriptReport(
            new ScriptRecord
            {
                FormId = 0x00008000,
                EditorId = "TestScript",
                VariableCount = 1,
                Variables = [new ScriptVariableInfo(0, "foo", 0)]
            },
            FormIdResolver.Empty);

        var section = Assert.Single(report.Sections, section => section.Name == "Variables");
        Assert.DoesNotContain(report.Sections,
            section => section.Name.StartsWith("Variables (", StringComparison.Ordinal));

        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "Variables").Value);
        Assert.Equal("1 variables", list.Display);
    }

    [Fact]
    public void Script_report_resolves_hardcoded_player_form_id_in_references()
    {
        var report = GeckScriptWriter.BuildScriptReport(
            new ScriptRecord
            {
                FormId = 0x00008000,
                EditorId = "TestScript",
                ReferencedObjects = [0x00000014]
            },
            FormIdResolver.Empty);

        var references = Assert.Single(report.Sections, section => section.Name == "References");
        var list = Assert.IsType<ReportValue.ListVal>(
            references.Fields.Single(field => field.Key == "Referenced Objects").Value);
        var playerRef = Assert.IsType<ReportValue.FormIdVal>(Assert.Single(list.Items));

        Assert.Equal(0x00000014u, playerRef.Raw);
        Assert.Equal("Player (0x00000014)", playerRef.Display);
    }

    [Fact]
    public void Perk_report_formats_actor_value_conditions_consistently()
    {
        var dmpStyle = BuildPermanentActorValueReport(new PerkCondition
        {
            FunctionIndex = 0x01EF,
            FunctionName = "GetPermanentActorValue",
            Parameter1 = 9,
            ComparisonOperator = 3,
            ComparisonValue = 4
        });
        var esmStyle = BuildPermanentActorValueReport(new PerkCondition
        {
            FunctionIndex = 0x01EF,
            FunctionName = "GetPermanentActorValue",
            Parameter1 = 9,
            Parameter1FormId = 9,
            ComparisonOperator = 3,
            ComparisonValue = 4
        });

        var dmpCondition = GetOnlyPerkConditionDisplay(dmpStyle);
        var esmCondition = GetOnlyPerkConditionDisplay(esmStyle);

        Assert.Equal("GetPermanentActorValue Intelligence >= 4", dmpCondition);
        Assert.Equal(dmpCondition, esmCondition);
    }

    private static RecordReport BuildPermanentActorValueReport(PerkCondition condition)
    {
        return GeckEffectsWriter.BuildPerkReport(
            new PerkRecord
            {
                FormId = 0x00009000,
                EditorId = "TestPerk",
                Conditions = [condition]
            },
            FormIdResolver.Empty);
    }

    private static string GetOnlyPerkConditionDisplay(RecordReport report)
    {
        var section = Assert.Single(report.Sections, section => section.Name == "Conditions (1)");
        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "Conditions").Value);
        var condition = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        return condition.Display;
    }

    [Fact]
    public void CrossDumpAggregator_adds_scripts_that_reference_npcs()
    {
        const uint npcFormId = 0x00007000;
        const uint scriptFormId = 0x00008000;
        const uint questFormId = 0x00009000;
        const string filePath = "npc-script-refs.esm";
        var records = new RecordCollection
        {
            Npcs =
            [
                new NpcRecord
                {
                    FormId = npcFormId,
                    EditorId = "MannyVargas"
                }
            ],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = scriptFormId,
                    EditorId = "VMS15MannyScript",
                    IsCompiled = true,
                    ReferencedObjects = [npcFormId],
                    OwnerQuestFormId = questFormId
                }
            ]
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [npcFormId] = "MannyVargas",
                [scriptFormId] = "VMS15MannyScript",
                [questFormId] = "VMS15"
            },
            [],
            []);

        var scriptReferenceIndexes = CrossDumpAggregator.BuildNpcScriptReferenceIndexes([(filePath, records)]);
        records.Scripts.Clear();
        var index = CrossDumpAggregator.Aggregate(
            [(filePath, records, resolver, null)],
            new HashSet<string>(["NPC"], StringComparer.OrdinalIgnoreCase),
            npcScriptReferenceIndexes: scriptReferenceIndexes);

        var report = index.StructuredRecords["NPC"][npcFormId][0];
        var section = Assert.Single(report.Sections, section => section.Name == "Referenced In");
        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "Scripts").Value);
        Assert.Equal("1 scripts", list.Display);

        var script = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        var fields = script.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(scriptFormId, Assert.IsType<ReportValue.FormIdVal>(fields["FormID"]).Raw);
        Assert.Equal("VMS15MannyScript", Assert.IsType<ReportValue.StringVal>(fields["Editor ID"]).Raw);
        Assert.Equal("Object", Assert.IsType<ReportValue.StringVal>(fields["Type"]).Raw);
        Assert.Equal(questFormId, Assert.IsType<ReportValue.FormIdVal>(fields["Owner Quest"]).Raw);
        Assert.Contains("VMS15MannyScript", script.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossDumpAggregator_adds_npc_placements_from_compact_cell_index()
    {
        const uint npcFormId = 0x00007000;
        const uint cellFormId = 0x00008000;
        const uint refFormId = 0x00009000;
        const string filePath = "placements.esm";
        var records = new RecordCollection
        {
            Npcs =
            [
                new NpcRecord
                {
                    FormId = npcFormId,
                    EditorId = "NpcGoodspringsDoc",
                    FullName = "Doc Mitchell"
                }
            ],
            Cells =
            [
                new CellRecord
                {
                    FormId = cellFormId,
                    EditorId = "GoodspringsDocHouse",
                    FullName = "Doc Mitchell's House",
                    WorldspaceFormId = 0x0000003C,
                    GridX = -1,
                    GridY = 2,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = refFormId,
                            BaseFormId = npcFormId,
                            BaseEditorId = "NpcGoodspringsDoc",
                            RecordType = "ACHR",
                            X = 12.5f,
                            Y = -30.2f,
                            Z = 128.0f,
                            RotZ = 1.25f,
                            IsPersistent = true
                        }
                    ]
                }
            ]
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [npcFormId] = "NpcGoodspringsDoc",
                [cellFormId] = "GoodspringsDocHouse",
                [0x0000003C] = "WastelandNV"
            },
            new Dictionary<uint, string>
            {
                [npcFormId] = "Doc Mitchell",
                [cellFormId] = "Doc Mitchell's House"
            },
            new Dictionary<uint, uint> { [refFormId] = npcFormId });

        var placementIndexes = CrossDumpAggregator.BuildNpcPlacementIndexes([(filePath, records)]);
        records.Cells.Clear();
        var index = CrossDumpAggregator.Aggregate(
            [(filePath, records, resolver, null)],
            new HashSet<string>(["NPC"], StringComparer.OrdinalIgnoreCase),
            npcPlacementIndexes: placementIndexes);

        var report = index.StructuredRecords["NPC"][npcFormId][0];
        var section = Assert.Single(report.Sections, section => section.Name == "Placements");
        Assert.DoesNotContain(section.Fields, field => field.Key == "Count");

        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "References").Value);
        Assert.Equal("1 references", list.Display);
        var placement = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        var fields = placement.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(refFormId, Assert.IsType<ReportValue.FormIdVal>(fields["FormID"]).Raw);
        Assert.Equal(cellFormId, Assert.IsType<ReportValue.FormIdVal>(fields["Cell"]).Raw);
        Assert.Equal(0x0000003Cu, Assert.IsType<ReportValue.FormIdVal>(fields["Worldspace"]).Raw);
        Assert.Equal("-1, 2", Assert.IsType<ReportValue.StringVal>(fields["Grid"]).Raw);
        Assert.Equal("(12.5, -30.2, 128.0)", Assert.IsType<ReportValue.StringVal>(fields["Position"]).Raw);
        Assert.True(Assert.IsType<ReportValue.BoolVal>(fields["Persistent"]).Raw);
        Assert.Contains("WastelandNV", placement.Display, StringComparison.Ordinal);
        Assert.Contains("Doc Mitchell's House", placement.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonJsRenderer_keys_facegen_controls_and_factions_by_identity()
    {
        Assert.Contains("if (fieldMap.Faction)", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("return 'Faction=' + scalarKeyText(fieldMap.Faction);",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("if (fieldMap.Control)", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("return 'Control=' + scalarKeyText(fieldMap.Control);",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("return renderGenericCompositeInlineDiff(curVal, prevVal);",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("function renderGenericCompositeInlineDiff(curVal, prevVal)",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("if (fieldMap.Item)", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("return 'Item=' + scalarKeyText(fieldMap.Item);",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("fieldMap.Control && fieldMap.Value", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("function canonicalSectionName(name)", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("insertSectionInReportOrder(sectionOrder, reportSectionNames, si)",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("function maybeWrapCollapsedField(sectionName, fieldKey, value, html)",
            ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("sectionName !== 'FaceGen Morph Data'", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("readChunkScriptPayload", ComparisonJsRenderer.Script, StringComparison.Ordinal);
        Assert.Contains("window.__comparisonExternalChunks", ComparisonJsRenderer.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_uses_chunked_page_for_grouped_cells_with_small_payload_limit()
    {
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new()
            {
                [0] = new RecordReport(
                    "Cell",
                    0x00005000,
                    "GoodspringsA",
                    "Goodsprings A",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            },
            [0x00005001] = new()
            {
                [0] = new RecordReport(
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

        // With a 1-byte inline limit, the chunked page path is taken instead of inline
        var files = CrossDumpJsonHtmlWriter.GenerateAll(index, 1);

        Assert.Contains("compare_cell.html", files.Keys);
        // Chunked pages use per-group script tags with data-group attributes
        Assert.Contains("data-group=", files["compare_cell.html"], StringComparison.Ordinal);
        Assert.Contains("chunked by group", files["compare_cell.html"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_uses_chunked_page_when_json_payload_exceeds_cell_limit()
    {
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new()
            {
                [0] = new RecordReport(
                    "Cell",
                    0x00005000,
                    "GoodspringsA",
                    "Goodsprings A",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            },
            [0x00005001] = new()
            {
                [0] = new RecordReport(
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

        // int.MaxValue inline limit but 1-byte JSON limit forces chunked path
        var files = CrossDumpJsonHtmlWriter.GenerateAll(
            index,
            int.MaxValue,
            1);

        Assert.Contains("compare_cell.html", files.Keys);
        Assert.Contains("data-group=", files["compare_cell.html"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossDumpJsonHtmlWriter_externalizes_streamed_chunk_payloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDir = CreateTempDirectory();
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));
        index.StructuredRecords["Cell"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new()
            {
                [0] = new RecordReport(
                    "Cell",
                    0x00005000,
                    "GoodspringsA",
                    "Goodsprings A",
                    [new ReportSection("Environment", [new ReportField("Interior", ReportValue.Bool(false))])])
            },
            [0x00005001] = new()
            {
                [0] = new RecordReport(
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

        var outputFile = await CrossDumpJsonHtmlWriter.WriteRecordTypeFileAsync(
            index,
            "Cell",
            outputDir,
            maxInlineCompressedPayloadLength: 1,
            maxCellJsonPayloadLength: 1,
            cancellationToken);

        Assert.Equal(Path.Combine(outputDir, "compare_cell.html"), outputFile);
        var html = await File.ReadAllTextAsync(outputFile!, cancellationToken);
        Assert.Contains("data-external-key=\"chunk-0\"", html, StringComparison.Ordinal);
        Assert.Contains("compare_cell_chunks/chunk-0.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"chunk-0\" data-group=\"Goodsprings\" data-z=", html,
            StringComparison.Ordinal);

        var chunkFile = Path.Combine(outputDir, "compare_cell_chunks", "chunk-0.js");
        Assert.True(File.Exists(chunkFile));
        var chunkScript = await File.ReadAllTextAsync(chunkFile, cancellationToken);
        Assert.Contains("window.__comparisonExternalChunks", chunkScript, StringComparison.Ordinal);

        var page = ComparisonBlobReader.Read(outputFile!);
        Assert.NotNull(page);
        Assert.Contains(0x00005000u, page!.Records.Keys);
        Assert.Contains(0x00005001u, page.Records.Keys);
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_chunks_large_ungrouped_record_types_without_synthetic_record_groups()
    {
        var index = new CrossDumpRecordIndex();
        index.Dumps.Add(new DumpSnapshot(
            "build_01.dmp",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "build_01",
            true));

        index.StructuredRecords["NPC"] = Enumerable.Range(0, 5_000)
            .ToDictionary(
                i => 0x00001000u + (uint)i,
                i => new Dictionary<int, RecordReport>
                {
                    [0] = new(
                        "NPC",
                        0x00001000u + (uint)i,
                        $"Npc{i:D4}",
                        $"NPC {i:D4}",
                        [new ReportSection("Identity", [new ReportField("Level", ReportValue.Int(i))])])
                });

        var files = CrossDumpJsonHtmlWriter.GenerateAll(index);

        Assert.Contains("compare_npc.html", files.Keys);
        Assert.Contains("records (chunked)", files["compare_npc.html"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("All Records", files["compare_npc.html"], StringComparison.Ordinal);
        Assert.DoesNotContain("Records 00001-01000", files["compare_npc.html"], StringComparison.Ordinal);
        Assert.Contains("data-group=", files["compare_npc.html"], StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonJsonBlobBuilder_streamed_chunk_payloads_preserve_all_records()
    {
        var formIdMap = Enumerable.Range(0, 6)
            .ToDictionary(
                i => 0x00002000u + (uint)i,
                i => new Dictionary<int, RecordReport>
                {
                    [0] = new(
                        "NPC",
                        0x00002000u + (uint)i,
                        $"Npc{i:D4}",
                        $"NPC {i:D4}",
                        [
                            new ReportSection("Identity",
                            [
                                new ReportField("Payload", ReportValue.String(new string('x', 300)))
                            ])
                        ])
                });
        var groups = formIdMap.Keys.ToDictionary(formId => formId, _ => "All Records");

        var chunks = ComparisonJsonBlobBuilder.BuildChunkPayloads(
                formIdMap,
                "NPC",
                groups,
                metadata: null,
                maxChunkBytes: 700)
            .ToList();

        Assert.True(chunks.Count > 1);
        Assert.Contains(chunks, chunk => chunk.GroupKey == "All Records (part 1)");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, compressedPayload) in chunks)
        {
            using var document = JsonDocument.Parse(InflateBase64(compressedPayload));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                seen.Add(property.Name);
            }
        }

        Assert.Equal(
            formIdMap.Keys.Select(formId => $"0x{formId:X8}").OrderBy(value => value, StringComparer.Ordinal),
            seen.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void ComparisonJsRenderer_uses_streaming_json_parse_for_embedded_payload()
    {
        Assert.Contains("function createInflatedReadable(bytes, format)", ComparisonJsRenderer.Script);
        Assert.Contains("new Blob([bytes]).stream().pipeThrough(new DecompressionStream(format))",
            ComparisonJsRenderer.Script);
        Assert.Contains("return new Response(createInflatedReadable(bytes, format)).json();",
            ComparisonJsRenderer.Script);
        Assert.Contains("setLoadingStatus('Inflating comparison data (' + format + ')...');",
            ComparisonJsRenderer.Script);
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
        Assert.Contains("function navigateToRecord(formId)", ComparisonJsRenderer.Script);
        Assert.Contains("async function navigateToHashRecord()", ComparisonJsRenderer.Script);
        Assert.Contains("function renderFormIdValue(val)", ComparisonJsRenderer.Script);
        Assert.Contains("function renderCellPageLink(val)", ComparisonJsRenderer.Script);
        Assert.Contains("compare_cell.html#", ComparisonJsRenderer.Script);
        Assert.Contains("summaryRow.id = recordDomId(formId);", ComparisonJsRenderer.Script);
        Assert.Contains("requestAnimationFrame(alignRenderedDetailRows);", ComparisonJsRenderer.Script);
        Assert.Contains("alignDetailSlots(detailRow, template.length);", ComparisonJsRenderer.Script);
        Assert.Contains("compositeFieldText(fieldMap, 'Position')", ComparisonJsRenderer.Script);
        Assert.Contains("compositeFieldText(fieldMap, 'Rotation')", ComparisonJsRenderer.Script);
        Assert.Contains("fieldMap['Links to']", ComparisonJsRenderer.Script);
        Assert.Contains("fieldMap['Containing Cell']", ComparisonJsRenderer.Script);
        Assert.Contains("var worldspaceVal = fieldMap.Worldspace;", ComparisonJsRenderer.Script);
        Assert.Contains("var cellVal = fieldMap.Cell;", ComparisonJsRenderer.Script);
        Assert.Contains("worldspace: ' + renderFormIdValue(worldspaceVal)", ComparisonJsRenderer.Script);
        Assert.Contains("cell: ' + renderCellReferenceValue(cellVal)", ComparisonJsRenderer.Script);
        Assert.Contains("rd-list-meta", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("JSON.parse(json)", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("await w.write(bytes);", ComparisonJsRenderer.Script);
        Assert.Contains("new TextDecoder().decode(bytes);", ComparisonJsRenderer.Script);
    }

    [Fact]
    public void ComparisonJsRenderer_build_sort_uses_dump_index_status_cells()
    {
        Assert.Contains("status-cell\" data-dump-idx=\"", ComparisonJsRenderer.Script);
        Assert.Contains("data-status=\"", ComparisonJsRenderer.Script);
        Assert.Contains("td.status-cell[data-dump-idx=\"", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("fixedCols + idx", ComparisonJsRenderer.Script);
        Assert.DoesNotContain("formIdDisplay", ComparisonJsRenderer.Script);
        Assert.Contains("Virtual cell aligned from", ComparisonJsRenderer.Script);
        Assert.Contains("getMetadataDumpValue", ComparisonJsRenderer.Script);
        Assert.Contains("visibleNameHistory(rec.editorIdHistory)", ComparisonJsRenderer.Script);
        Assert.Contains("isSyntheticVirtualLabel(value)", ComparisonJsRenderer.Script);
        Assert.Contains("return 'FormID=' + scalarKeyText(fieldMap.FormID);", ComparisonJsRenderer.Script);
        Assert.Contains("return 'Index=' + scalarKeyText(fieldMap.Index);", ComparisonJsRenderer.Script);
        Assert.Contains("renderRemovedListItemsUntil(prevItems, curKeys,", ComparisonJsRenderer.Script);
        Assert.Contains("prevCursor = matchIndex + 1;", ComparisonJsRenderer.Script);
        Assert.Contains("function displayFieldKey(key)", ComparisonJsRenderer.Script);
        Assert.Contains("return key === 'FormID' ? 'Form ID' : key;", ComparisonJsRenderer.Script);
        Assert.Contains("compareRecordsForDefaultOrder(", ComparisonJsRenderer.Script);
        Assert.Contains("compareEditorValues(", ComparisonJsRenderer.Script);
        Assert.Contains("if (aBlank !== bBlank) return aBlank ? 1 : -1;", ComparisonJsRenderer.Script);
        Assert.Contains("return compareCoordinateNumbers(ax, ay, bx, by);", ComparisonJsRenderer.Script);
        Assert.Contains("if (ay !== by) return by - ay;", ComparisonJsRenderer.Script);
        Assert.Contains("sectionOrder = orderDetailSections(sectionOrder);", ComparisonJsRenderer.Script);
        Assert.Contains("if (name === 'Environment') return 10;", ComparisonJsRenderer.Script);
        Assert.Contains("if (name === 'Placed Objects') return 40;", ComparisonJsRenderer.Script);
        Assert.Contains("detailRow.style.display = '';", ComparisonJsRenderer.Script);
        Assert.Contains("summaryRow.classList.add('expanded');", ComparisonJsRenderer.Script);
        Assert.Contains("renderDetail(detailRow);", ComparisonJsRenderer.Script);
        Assert.Contains(".summary-row.expanded", ComparisonCssStyles.Styles);
    }

    [Fact]
    public void CrossDumpJsonHtmlWriter_emits_sparse_raw_dump_indices_for_late_visible_builds()
    {
        var index = new CrossDumpRecordIndex();
        for (var i = 0; i < 5; i++)
        {
            index.Dumps.Add(new DumpSnapshot(
                $"build_{i + 1:D2}.dmp",
                new DateTime(2024, 1, i + 1, 0, 0, 0, DateTimeKind.Utc),
                $"build_{i + 1:D2}",
                true));
        }

        index.StructuredRecords["Weapon"] = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00004000] = new()
            {
                [0] = new RecordReport(
                    "Weapon",
                    0x00004000,
                    "WeapTest",
                    "Test Weapon",
                    [new ReportSection("Stats", [new ReportField("Damage", ReportValue.Int(12))])]),
                [4] = new RecordReport(
                    "Weapon",
                    0x00004000,
                    "WeapTest",
                    "Test Weapon",
                    [new ReportSection("Stats", [new ReportField("Damage", ReportValue.Int(14))])])
            }
        };

        var files = CrossDumpJsonHtmlWriter.GenerateAll(index);
        var html = files["compare_weapon.html"];
        var json = InflateBase64(ExtractCompressedPayload(html));

        using var document = JsonDocument.Parse(json);
        var sparseDumps = document.RootElement.GetProperty("sparseDumps")
            .EnumerateArray()
            .Select(element => element.GetInt32())
            .ToArray();
        var present = document.RootElement
            .GetProperty("records")
            .GetProperty("0x00004000")
            .GetProperty("present")
            .EnumerateArray()
            .Select(element => element.GetInt32())
            .ToArray();

        Assert.Equal([1, 2, 3], sparseDumps);
        Assert.Equal([0, 4], present);
        Assert.Contains("status-cell\" data-dump-idx=\"", html);
        Assert.Contains("td.status-cell[data-dump-idx=\"", html);
    }

    [Fact]
    public void ComparisonJsonBlobBuilder_omits_blank_and_virtual_labels_from_name_history()
    {
        var formIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [0x00005000] = new()
            {
                [0] = new RecordReport(
                    "Cell",
                    0x00005000,
                    "",
                    "[Virtual 4,27 WastelandNV]",
                    [new ReportSection("Identity", [new ReportField("FormID", ReportValue.String("0x00005000"))])]),
                [1] = new RecordReport(
                    "Cell",
                    0x00005000,
                    "GoodspringsSource",
                    "",
                    [new ReportSection("Identity", [new ReportField("FormID", ReportValue.String("0x00005000"))])])
            }
        };
        var dumps = new List<DumpSnapshot>
        {
            new("build_01.dmp", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "build_01", true),
            new("build_02.dmp", new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), "build_02", true)
        };

        var json = ComparisonJsonBlobBuilder.Build(
            formIdMap,
            dumps,
            "Cell",
            null,
            null,
            null,
            null,
            null);

        using var document = JsonDocument.Parse(json);
        var record = document.RootElement.GetProperty("records").GetProperty("0x00005000");
        Assert.Equal("GoodspringsSource", record.GetProperty("editorId").GetString());
        Assert.Null(record.GetProperty("displayName").GetString());
        Assert.False(record.TryGetProperty("editorIdHistory", out _));
        Assert.False(record.TryGetProperty("nameHistory", out _));
    }

    [Fact]
    public void WorldHeightNormalizer_clamps_invalid_reportable_heights_to_zero()
    {
        Assert.Equal(0f, WorldHeightNormalizer.NormalizeReportableHeight(float.MaxValue));
        Assert.Equal(0f, WorldHeightNormalizer.NormalizeReportableHeight(150_000f));
        Assert.Equal(42.5f, WorldHeightNormalizer.NormalizeReportableHeight(42.5f));
        Assert.Equal(-42.5f, WorldHeightNormalizer.NormalizeReportableHeight(-42.5f));
        Assert.Null(WorldHeightNormalizer.NormalizeReportableHeight((float?)null));
    }

    [Fact]
    public void GeckWorldWriter_clamps_present_invalid_water_heights_in_reports()
    {
        var resolver = new FormIdResolver([], [], []);
        var cellReport = GeckWorldWriter.BuildCellReport(
            new CellRecord
            {
                FormId = 0x00005000,
                EditorId = "BadWaterCell",
                WaterHeight = float.MaxValue
            },
            resolver);
        var worldspaceReport = GeckWorldWriter.BuildWorldspaceReport(
            new WorldspaceRecord
            {
                FormId = 0x00005010,
                EditorId = "BadWaterWorld",
                DefaultWaterHeight = 340282346638528859811704183484516925440.0f
            },
            resolver);

        var cellWater = Assert.IsType<ReportValue.FloatVal>(
            cellReport.Sections.Single(section => section.Name == "Environment")
                .Fields.Single(field => field.Key == "Water Height").Value);
        var worldWater = Assert.IsType<ReportValue.FloatVal>(
            worldspaceReport.Sections.Single(section => section.Name == "Heights")
                .Fields.Single(field => field.Key == "Default Water Height").Value);

        Assert.Equal(0, cellWater.Raw);
        Assert.Equal(0, worldWater.Raw);
        Assert.DoesNotContain("340282346638528", cellWater.Display);
        Assert.DoesNotContain("340282346638528", worldWater.Display);
    }

    [Fact]
    public void GeckWorldWriter_keeps_missing_water_heights_missing()
    {
        var report = GeckWorldWriter.BuildCellReport(
            new CellRecord
            {
                FormId = 0x00005000,
                EditorId = "DryCell"
            },
            new FormIdResolver([], [], []));

        Assert.DoesNotContain(
            report.Sections.SelectMany(section => section.Fields),
            field => field.Key == "Water Height");
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
        var placedObjects = Assert.Single(report.Sections, section => section.Name == "Placed Objects");
        var objectsField = Assert.Single(placedObjects.Fields, field => field.Key == "Objects");
        var objectList = Assert.IsType<ReportValue.ListVal>(objectsField.Value);
        var objectItem = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(objectList.Items));
        var fields = objectItem.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal("(1.0, 2.0, 3.0)", Assert.IsType<ReportValue.StringVal>(fields["Position"]).Raw);
        Assert.Equal("(0.000, 0.000, 0.500)", Assert.IsType<ReportValue.StringVal>(fields["Rotation"]).Raw);
        Assert.True(Assert.IsType<ReportValue.BoolVal>(fields["Disabled"]).Raw);
    }

    [Fact]
    public void GeckWorldWriter_builds_cell_report_with_door_links_and_reference_editor_ids()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00007000] = "GoodspringsDoorRef",
                [0x00008000] = "DoorGoodsprings",
                [0x00009000] = "Sloan"
            },
            [],
            []);
        var cell = new CellRecord
        {
            FormId = 0x00006000,
            EditorId = "Goodsprings",
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0x00007000,
                    BaseFormId = 0x00008000,
                    BaseEditorId = "DoorGoodsprings",
                    EditorId = "GoodspringsDoorRef",
                    RecordType = "REFR",
                    DestinationDoorFormId = 0x00007010,
                    DestinationCellFormId = 0x00009000
                }
            ]
        };

        var report = GeckWorldWriter.BuildCellReport(cell, resolver);
        var objectItem = Assert.IsType<ReportValue.CompositeVal>(
            Assert.Single(Assert.IsType<ReportValue.ListVal>(
                Assert.Single(report.Sections, section => section.Name == "Placed Objects")
                    .Fields.Single(field => field.Key == "Objects").Value).Items));
        var fields = objectItem.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal("GoodspringsDoorRef", Assert.IsType<ReportValue.StringVal>(fields["Reference Editor ID"]).Raw);
        Assert.Equal(0x00009000u, Assert.IsType<ReportValue.FormIdVal>(fields["Links to"]).Raw);
        Assert.Equal(0x00007010u, Assert.IsType<ReportValue.FormIdVal>(fields["Destination Door"]).Raw);
        Assert.Contains("Links to:", objectItem.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void GeckWorldWriter_cell_door_links_list_destination_doors_not_raw_linked_cells()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00007000] = "SourceDoorRef",
                [0x00007010] = "DestinationDoorRef",
                [0x00008000] = "DoorGoodsprings",
                [0x00009000] = "Sloan"
            },
            [],
            []);
        var sourceDoor = new PlacedReference
        {
            FormId = 0x00007000,
            BaseFormId = 0x00008000,
            BaseEditorId = "DoorGoodsprings",
            EditorId = "SourceDoorRef",
            RecordType = "REFR",
            DestinationDoorFormId = 0x00007010,
            DestinationCellFormId = 0x00009000
        };
        var destinationDoor = new PlacedReference
        {
            FormId = 0x00007010,
            BaseFormId = 0x00008000,
            BaseEditorId = "DoorGoodsprings",
            EditorId = "DestinationDoorRef",
            RecordType = "REFR",
            X = 10,
            Y = 20,
            Z = 30,
            DestinationDoorFormId = 0x00007000,
            DestinationCellFormId = 0x00006000
        };
        var cell = new CellRecord
        {
            FormId = 0x00006000,
            EditorId = "Goodsprings",
            LinkedCellFormIds = [0xFE800123],
            PlacedObjects = [sourceDoor]
        };
        var locations = new Dictionary<uint, PlacedReferenceLocation>
        {
            [destinationDoor.FormId] = new(destinationDoor, 0x00009000)
        };

        var report = GeckWorldWriter.BuildCellReport(cell, resolver, locations);

        Assert.DoesNotContain(report.Sections, section => section.Name == "Linked Cells");
        var doorLinks = Assert.Single(report.Sections, section => section.Name == "Door Links");
        var linkItem = Assert.IsType<ReportValue.CompositeVal>(
            Assert.Single(Assert.IsType<ReportValue.ListVal>(
                Assert.Single(doorLinks.Fields).Value).Items));
        var fields = linkItem.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(0x00007010u, Assert.IsType<ReportValue.FormIdVal>(fields["FormID"]).Raw);
        Assert.Equal(0x00009000u, Assert.IsType<ReportValue.FormIdVal>(fields["Containing Cell"]).Raw);
        Assert.Equal(0x00007000u, Assert.IsType<ReportValue.FormIdVal>(fields["Linked From"]).Raw);
    }

    [Fact]
    public void RecordDetailPresenter_cell_includes_door_links_label()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00008000] = "DoorGoodsprings",
                [0x00009000] = "Sloan"
            },
            [],
            []);
        var cell = new CellRecord
        {
            FormId = 0x00006000,
            EditorId = "Goodsprings",
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0x00007000,
                    BaseFormId = 0x00008000,
                    BaseEditorId = "DoorGoodsprings",
                    RecordType = "REFR",
                    DestinationCellFormId = 0x00009000
                }
            ]
        };

        Assert.True(RecordDetailPresenter.TryBuildForRecord(cell, null, resolver, out var model));
        var doorLinks = Assert.Single(model!.Sections, section => section.Title == "Door Links");
        var item = Assert.Single(Assert.Single(doorLinks.Entries).Items!);

        Assert.Contains("DoorGoodsprings", item.Label, StringComparison.Ordinal);
        Assert.Equal("Links to: Sloan (0x00009000)", item.Value);
    }

    [Fact]
    public void ESM_pipeline_redistributes_persistent_cell_grup_refs_to_exterior_cells()
    {
        const uint worldspaceFormId = 0x00001000;
        const uint persistentCellFormId = 0x00002000;
        const uint exteriorCellFormId = 0x00003000;
        const uint refFormId = 0x00004000;
        const uint baseFormId = 0x00005000;

        var pipeline = new EsmTestFileBuilder()
            .AddTopLevelGrup("STAT",
                EsmTestFileBuilder.BuildRecord(
                    "STAT",
                    baseFormId,
                    0,
                    ("EDID", NullTerm("TestXMarker"))))
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = worldspaceFormId,
                EditorId = "TestWorld",
                PersistentCell = new EsmTestFileBuilder.CellData
                {
                    FormId = persistentCellFormId,
                    EditorId = "TestWorldPersistent",
                    PersistentRefs =
                    [
                        new EsmTestFileBuilder.PlacedRefData
                        {
                            RecordType = "REFR",
                            FormId = refFormId,
                            BaseFormId = baseFormId,
                            Flags = 0x00000400,
                            EditorId = "PersistentMarkerRef",
                            X = 4096f + 128f,
                            Y = -4096f + 128f,
                            Z = 0f
                        }
                    ]
                },
                ExteriorCells =
                [
                    new EsmTestFileBuilder.CellData
                    {
                        FormId = exteriorCellFormId,
                        EditorId = "ExteriorTile",
                        GridX = 1,
                        GridY = -1
                    }
                ]
            })
            .BuildAndAnalyze();

        var persistentCell = Assert.Single(pipeline.Collection.Cells, cell => cell.FormId == persistentCellFormId);
        var exteriorCell = Assert.Single(pipeline.Collection.Cells, cell => cell.FormId == exteriorCellFormId);
        var placedRef = Assert.Single(exteriorCell.PlacedObjects, obj => obj.FormId == refFormId);

        Assert.True(persistentCell.IsPersistentCell);
        Assert.Empty(persistentCell.PlacedObjects);
        Assert.Equal("PersistentMarkerRef", placedRef.EditorId);
        Assert.Equal(persistentCellFormId, placedRef.OriginCellFormId);
        Assert.Equal("PersistentRedistributed", placedRef.AssignmentSource);
    }

    [Fact]
    public void GeckWorldWriter_keeps_virtual_cell_environment_separate_from_placed_objects()
    {
        var resolver = new FormIdResolver([], [], []);
        var report = RecordTextFormatter.BuildReport(
            new CellRecord
            {
                FormId = 0xFF000001,
                EditorId = "[Virtual 4,-2 WastelandNV]",
                GridX = 4,
                GridY = -2,
                WorldspaceFormId = 0x00000010,
                IsVirtual = true,
                WaterHeight = 10f,
                AcousticSpaceFormId = 0x00000020,
                LightingTemplateFormId = 0x00000030,
                LightingTemplateInheritanceFlags = 0x00000040,
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
                        Z = 3.0f
                    }
                ]
            },
            resolver);

        Assert.NotNull(report);
        var sectionNames = report.Sections.Select(section => section.Name).ToArray();
        Assert.Equal(["Identity", "Environment", "Placed Objects"], sectionNames);

        var environment = Assert.Single(report.Sections, section => section.Name == "Environment");
        var placedObjects = Assert.Single(report.Sections, section => section.Name == "Placed Objects");

        Assert.Contains(environment.Fields, field => field.Key == "Water Height");
        Assert.Contains(environment.Fields, field => field.Key == "Acoustic Space");
        Assert.Contains(environment.Fields, field => field.Key == "Lighting Template");
        Assert.Contains(environment.Fields, field => field.Key == "Lighting Inheritance Flags");
        Assert.DoesNotContain(placedObjects.Fields, field => field.Key == "Water Height");
        Assert.Single(placedObjects.Fields, field => field.Key == "Objects");
    }

    [Fact]
    public void RecordTextFormatter_includes_structured_note_reports()
    {
        var records = new RecordCollection
        {
            Notes =
            [
                new NoteRecord
                {
                    FormId = 0x00003100,
                    EditorId = "NoteVault",
                    FullName = "Vault Note",
                    Text = "Remember the password.",
                    NoteType = 0,
                    ModelPath = @"meshes\clutter\note.nif",
                    IconPath = @"interface\icons\note.dds",
                    SoundFormId = 0x00004400
                }
            ]
        };

        var (typeName, _, _, _, record) = Assert.Single(RecordTextFormatter.EnumerateAll(records));
        Assert.Equal("Note", typeName);

        var report = RecordTextFormatter.BuildReport(record, FormIdResolver.Empty);

        Assert.NotNull(report);
        Assert.Equal("Note", report.RecordType);
        Assert.Contains(report.Sections, section => section.Name == "Content");
        Assert.Contains(report.Sections.Single(section => section.Name == "Content").Fields,
            field => field.Key == "Text" && field.Value.Display.Contains("password", StringComparison.Ordinal));
        Assert.Contains(report.Sections.Single(section => section.Name == "Art Assets").Fields,
            field => field.Key == "Inventory Icon");
        Assert.Contains(report.Sections.Single(section => section.Name == "References").Fields,
            field => field.Key == "Audio");
    }

    [Fact]
    public void RecordReportComparer_treats_one_sided_sections_as_changed()
    {
        var withoutContent = new RecordReport(
            "Note",
            0x00003100,
            "NoteVault",
            "Vault Note",
            [new ReportSection("Identity", [new ReportField("Type", ReportValue.String("Text"))])]);
        var withContent = new RecordReport(
            "Note",
            0x00003100,
            "NoteVault",
            "Vault Note",
            [
                new ReportSection("Identity", [new ReportField("Type", ReportValue.String("Text"))]),
                new ReportSection("Content", [new ReportField("Text", ReportValue.String("Remember the password."))])
            ]);

        Assert.False(RecordReportComparer.Equals(withoutContent, withContent));
    }

    [Fact]
    public void Comparison_renderer_uses_text_styling_for_note_content()
    {
        Assert.Contains("shouldRenderTextContent", ComparisonJsRenderer.Script);
        Assert.Contains("rd-text", ComparisonJsRenderer.Script);
        Assert.Contains(".rd-text", ComparisonCssStyles.Styles);
    }

    [Fact]
    public void Recipe_report_lists_components_and_results_as_form_ids()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00001000] = "ScrapMetal",
                [0x00002000] = "WeaponRepairKit"
            },
            new Dictionary<uint, string>
            {
                [0x00001000] = "Scrap Metal",
                [0x00002000] = "Weapon Repair Kit"
            },
            []);
        var report = GeckItemDetailWriter.BuildRecipeReport(
            new RecipeRecord
            {
                FormId = 0x00003000,
                EditorId = "RecipeRepairKit",
                FullName = "Weapon Repair Kit",
                RequiredSkill = -1,
                Ingredients = [new RecipeIngredient { ItemFormId = 0x00001000, Count = 2 }],
                Outputs = [new RecipeOutput { ItemFormId = 0x00002000, Count = 1 }]
            },
            resolver);

        var ingredients = Assert.Single(report.Sections, section => section.Name == "Ingredients");
        var outputs = Assert.Single(report.Sections, section => section.Name == "Outputs");
        var component = Assert.IsType<ReportValue.CompositeVal>(
            Assert.IsType<ReportValue.ListVal>(ingredients.Fields.Single(field => field.Key == "Components").Value)
                .Items.Single());
        var result = Assert.IsType<ReportValue.CompositeVal>(
            Assert.IsType<ReportValue.ListVal>(outputs.Fields.Single(field => field.Key == "Results").Value)
                .Items.Single());

        Assert.IsType<ReportValue.FormIdVal>(component.Fields.Single(field => field.Key == "Item").Value);
        Assert.IsType<ReportValue.FormIdVal>(result.Fields.Single(field => field.Key == "Item").Value);
    }

    [Fact]
    public void Dialog_topic_report_includes_player_prompt()
    {
        var report = GeckDialogueWriter.BuildDialogTopicReport(
            new DialogTopicRecord
            {
                FormId = 0x00004500,
                EditorId = "TopicGreeting",
                FullName = "Who are you?",
                DummyPrompt = "Who are you?"
            },
            FormIdResolver.Empty);

        var prompt = Assert.Single(report.Sections, section => section.Name == "Prompt");
        Assert.Contains(prompt.Fields, field => field.Key == "Player" && field.Value.Display == "\"Who are you?\"");
    }

    [Fact]
    public void Map_marker_report_includes_containing_cell()
    {
        var marker = new PlacedReference
        {
            FormId = 0x00005000,
            BaseFormId = 0x00000010,
            BaseEditorId = "MapMarker",
            MarkerName = "Goodsprings",
            IsMapMarker = true
        };
        var report = RecordTextFormatter.BuildReport(
            marker,
            FormIdResolver.Empty,
            placedReferenceLocations: new Dictionary<uint, PlacedReferenceLocation>
            {
                [marker.FormId] = new(marker, 0x00006000)
            });

        Assert.NotNull(report);
        var location = Assert.Single(report.Sections, section => section.Name == "Location");
        var cell = Assert.Single(location.Fields, field => field.Key == "Cell");
        Assert.IsType<ReportValue.FormIdVal>(cell.Value);
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
            (new WeaponRecord { FormId = 2, EditorId = "WeapRifle", FullName = "Rifle", AmmoFormId = 0x20 }, "Ammo",
                0x20),
            (new QuestRecord { FormId = 3, EditorId = "QuestMain", FullName = "Main Quest", Script = 0x30 }, "Script",
                0x30),
            (new PackageRecord
            {
                FormId = 4,
                EditorId = "PackGuard",
                Data = new PackageData(),
                UseWeaponData = new PackageUseWeaponData { WeaponFormId = 0x40 }
            }, "Weapon", 0x40),
            (new CellRecord { FormId = 5, EditorId = "GoodspringsCell", FullName = "Goodsprings", WorldspaceFormId = 0x50 },
                "Worldspace", 0x50)
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

    private static byte[] UInt32Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] FloatLe(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
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
