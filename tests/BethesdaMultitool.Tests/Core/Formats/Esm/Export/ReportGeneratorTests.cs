using System.Globalization;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;
using EsmAnalyzer.Commands.Dmp;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     Smoke tests for GeckReportGenerator, CsvActorWriter, and CsvItemWriter.
///     These tests anchor behavior before the partial class elimination refactoring.
///     The "Script source labels" region pins how script text is labelled by provenance in the GECK script
///     report, <c>GeckScriptWriter.BuildScriptReport</c>, show QUST and EsmAnalyzer <c>dmp scripts</c>.
/// </summary>
public class ReportGeneratorTests
{
    #region Test Data

    private static RecordCollection MinimalRecords()
    {
        return new RecordCollection
        {
            Npcs =
            [
                new NpcRecord
                {
                    FormId = 0x00100000,
                    EditorId = "TestNpc",
                    FullName = "Test NPC"
                }
            ],
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x00200000,
                    EditorId = "TestWeapon",
                    FullName = "Test Weapon",
                    Damage = 25,
                    Weight = 3.5f,
                    Value = 100,
                    Speed = 1.0f,
                    ShotsPerSec = 2.0f
                }
            ],
            FormIdToEditorId = new Dictionary<uint, string>
            {
                [0x00100000] = "TestNpc",
                [0x00200000] = "TestWeapon"
            }
        };
    }

    private static RecordCollection EmptyRecords()
    {
        return new RecordCollection();
    }

    #endregion

    #region GeckReportGenerator.Generate

    [Fact]
    public void Generate_WithMinimalRecords_ReturnsNonEmptyReport()
    {
        var records = MinimalRecords();
        var report = GeckReportGenerator.Generate(records);

        Assert.NotEmpty(report);
        Assert.Contains("ESM Memory Dump Semantic Parse Report", report);
    }

    [Fact]
    public void Generate_WithMinimalRecords_ContainsSummarySection()
    {
        var records = MinimalRecords();
        var report = GeckReportGenerator.Generate(records);

        Assert.Contains("Summary:", report);
        Assert.Contains("NPCs:", report);
        Assert.Contains("Weapons:", report);
    }

    [Fact]
    public void Generate_WithNpc_ContainsNpcSection()
    {
        var records = MinimalRecords();
        var report = GeckReportGenerator.Generate(records);

        Assert.Contains("TestNpc", report);
        Assert.Contains("Test NPC", report);
    }

    [Fact]
    public void Generate_WithWeapon_ContainsWeaponSection()
    {
        var records = MinimalRecords();
        var report = GeckReportGenerator.Generate(records);

        Assert.Contains("TestWeapon", report);
        Assert.Contains("Test Weapon", report);
    }

    [Fact]
    public void Generate_WithEmptyRecords_ReturnsHeaderOnly()
    {
        var records = EmptyRecords();
        var report = GeckReportGenerator.Generate(records);

        Assert.NotEmpty(report);
        Assert.Contains("Summary:", report);
        Assert.DoesNotContain("TestNpc", report);
    }

    #endregion

    #region GeckReportGenerator.GenerateAllReports

    [Fact]
    public void GenerateAllReports_WithMinimalRecords_ContainsSummaryFile()
    {
        var sources = new ReportDataSources(MinimalRecords());
        var files = GeckReportGenerator.GenerateAllReports(sources);

        Assert.True(files.ContainsKey("summary.txt"));
        Assert.Contains("Summary:", files["summary.txt"]);
    }

    [Fact]
    public void GenerateAllReports_WithNpc_ContainsNpcFiles()
    {
        var sources = new ReportDataSources(MinimalRecords());
        var files = GeckReportGenerator.GenerateAllReports(sources);

        Assert.True(files.ContainsKey("npcs.csv"));
        Assert.True(files.ContainsKey("npc_report.txt"));
    }

    [Fact]
    public void GenerateAllReports_WithWeapon_ContainsWeaponFiles()
    {
        var sources = new ReportDataSources(MinimalRecords());
        var files = GeckReportGenerator.GenerateAllReports(sources);

        Assert.True(files.ContainsKey("weapons.csv"));
        Assert.True(files.ContainsKey("weapon_report.txt"));
    }

    [Fact]
    public void GenerateAllReports_WithEmptyRecords_ContainsOnlySummary()
    {
        var sources = new ReportDataSources(EmptyRecords());
        var files = GeckReportGenerator.GenerateAllReports(sources);

        Assert.Single(files);
        Assert.True(files.ContainsKey("summary.txt"));
    }

    [Fact]
    public void GenerateAllReports_WithStringOwnership_EmitsOwnershipFiles()
    {
        var ownership = new RuntimeStringOwnershipAnalysis();
        var unknown = new RuntimeStringHit
        {
            Text = @"meshes\props\crate01.nif",
            Category = StringCategory.FilePath,
            GapClassification = GapClassification.StringPool,
            FileOffset = 0x120,
            VirtualAddress = 0x40120,
            Length = 24,
            OwnershipStatus = RuntimeStringOwnershipStatus.ReferencedOwnerUnknown,
            InboundPointerCount = 2,
            OwnerResolution = new RuntimeStringOwnerResolution
            {
                ReferrerFileOffset = 0x200,
                ReferrerVa = 0x40200,
                ReferrerContext = "PointerDense:synthetic"
            }
        };
        var unreferenced = new RuntimeStringHit
        {
            Text = "TestEditor_One",
            Category = StringCategory.EditorId,
            GapClassification = GapClassification.AsciiText,
            FileOffset = 0x160,
            VirtualAddress = 0x40160,
            Length = 14,
            OwnershipStatus = RuntimeStringOwnershipStatus.Unreferenced
        };

        ownership.AllHits.Add(unknown);
        ownership.AllHits.Add(unreferenced);
        ownership.ReferencedOwnerUnknownHits.Add(unknown);
        ownership.UnreferencedHits.Add(unreferenced);
        ownership.CategoryCounts[StringCategory.FilePath] = 1;
        ownership.CategoryCounts[StringCategory.EditorId] = 1;
        ownership.StatusCounts[RuntimeStringOwnershipStatus.ReferencedOwnerUnknown] = 1;
        ownership.StatusCounts[RuntimeStringOwnershipStatus.Unreferenced] = 1;

        var files = GeckReportGenerator.GenerateAllReports(
            new ReportDataSources(EmptyRecords(), StringOwnership: ownership));

        Assert.Contains("string_ownership_summary.txt", files.Keys);
        Assert.Contains("string_unknown_owners.csv", files.Keys);
        Assert.Contains("string_unreferenced.csv", files.Keys);
        Assert.Contains("Referenced, owner unknown", files["string_ownership_summary.txt"]);
    }

    [Fact]
    public void GenerateAllReports_WithoutStringOwnership_DoesNotEmitOwnershipFiles()
    {
        var files = GeckReportGenerator.GenerateAllReports(new ReportDataSources(EmptyRecords()));

        Assert.DoesNotContain("string_ownership_summary.txt", files.Keys);
        Assert.DoesNotContain("string_unknown_owners.csv", files.Keys);
        Assert.DoesNotContain("string_unreferenced.csv", files.Keys);
    }

    #endregion

    #region CsvItemWriter / CsvActorWriter

    [Fact]
    public void GenerateNpcsCsv_ContainsHeaderRow()
    {
        var records = MinimalRecords();
        var csv = CsvActorWriter.GenerateNpcsCsv(records.Npcs, records.CreateResolver());

        Assert.Contains("RowType,FormID,EditorID,Name", csv);
    }

    [Fact]
    public void GenerateNpcsCsv_ContainsNpcData()
    {
        var records = MinimalRecords();
        var csv = CsvActorWriter.GenerateNpcsCsv(records.Npcs, records.CreateResolver());

        Assert.Contains("TestNpc", csv);
        Assert.Contains("0x00100000", csv);
    }

    [Fact]
    public void GenerateNpcsCsv_KeepsFractionalKarmaInOneColumnUnderCommaDecimalCulture()
    {
        var records = MinimalRecords();
        records.Npcs[0] = records.Npcs[0] with
        {
            Stats = new ActorBaseSubrecord(0, 0, 0, 1, 0, 0, 100, -125.5f, 0, 0, 0, false)
        };
        var previousCulture = CultureInfo.CurrentCulture;
        string csv;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            csv = CsvActorWriter.GenerateNpcsCsv(records.Npcs, records.CreateResolver());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        // The fixture contains no quoted commas, so any extra column is a decimal separator leak.
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var header = lines[0].TrimEnd('\r').Split(',');
        var row = lines[1].TrimEnd('\r').Split(',');
        Assert.Equal(header.Length, row.Length);
        var karmaIndex = Array.IndexOf(header, "Karma");
        Assert.NotEqual(-1, karmaIndex);
        Assert.Equal("-125.5", row[karmaIndex]);
    }

    [Fact]
    public void GenerateWeaponsCsv_ContainsHeaderRow()
    {
        var records = MinimalRecords();
        var csv = CsvItemWriter.GenerateWeaponsCsv(records.Weapons, records.CreateResolver());

        Assert.Contains("RowType,FormID,EditorID,Name", csv);
    }

    [Fact]
    public void GenerateWeaponsCsv_ContainsWeaponData()
    {
        var records = MinimalRecords();
        var csv = CsvItemWriter.GenerateWeaponsCsv(records.Weapons, records.CreateResolver());

        Assert.Contains("TestWeapon", csv);
        Assert.Contains("0x00200000", csv);
    }

    #endregion

    #region Helper Methods

    [Theory]
    [InlineData(-800f, "Very Evil")]
    [InlineData(-500f, "Evil")]
    [InlineData(0f, "Neutral")]
    [InlineData(500f, "Good")]
    [InlineData(800f, "Very Good")]
    public void FormatKarmaLabel_ReturnsCorrectLabel(float karma, string expectedLabel)
    {
        // FormatKarmaLabel is private — test indirectly via GenerateAllReports → npc_report.txt
        // which calls AppendNpcReportEntry → FormatKarmaLabel
        var records = new RecordCollection
        {
            Npcs =
            [
                new NpcRecord
                {
                    FormId = 0x00100000,
                    EditorId = "KarmaTestNpc",
                    Stats = new ActorBaseSubrecord(0, 0, 0, 1, 0, 0, 100, karma, 0, 0, 0, false)
                }
            ]
        };

        var sources = new ReportDataSources(records);
        var files = GeckReportGenerator.GenerateAllReports(sources);
        Assert.Contains(expectedLabel, files["npc_report.txt"]);
    }

    [Fact]
    public void CleanAssetPath_TestedViaAssetListReport()
    {
        var assets = new List<DetectedAssetString>
        {
            new() { Path = @"meshes\weapons\pistol.nif", Category = AssetCategory.Model },
            new() { Path = @"textures\armor\helmet.dds", Category = AssetCategory.Texture }
        };

        var report = GeckMiscWriter.GenerateAssetListReport(assets);
        Assert.Contains("weapons", report);
        Assert.Contains("pistol.nif", report);
        Assert.Contains("helmet.dds", report);
    }

    #endregion

    #region Script source labels (GECK script report, BuildScriptReport, show QUST, EsmAnalyzer dmp scripts)

    // One rule, pinned across every presenter this group owns: "With Source (SCTX)" and the "Source (SCTX)"
    // heading mean text a script author wrote (plugin SCTX, or SCTX recovered from a memory dump). A memory-dump
    // SCTX that is a BethesdaMultitool decompilation of the record's own SCDA is labelled a reconstruction.
    // Before this, the Feb 2010 dump's script_report.txt said "With Source (SCTX): 1,311" and headed all 1,311
    // reconstructions "Source (SCTX):"; only an in-text banner revealed them.

    private const string AbsenceWordingLiteral =
        "not present in this capture; absence from a partial memory dump is not evidence of absence from the build";

    private const string ReconstructionLabelLiteral =
        "Reconstruction (SCDA)";

    /// <summary>
    ///     A memory-dump pair: one SCTX recovered from a dump fragment (accepted by the same-dump correspondence
    ///     gate) and one SCTX that the emission contract synthesized from SCDA because no proven source survived.
    /// </summary>
    private static List<ScriptRecord> DumpScripts()
    {
        return
        [
            new ScriptRecord
            {
                FormId = 0x00AB1001,
                EditorId = "AlphaCapturedSCRIPT",
                SourceText = "scn AlphaCapturedSCRIPT\r\nBegin GameMode\r\nEnd",
                SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                DecompiledText = "ScriptName AlphaCapturedSCRIPT\nBegin GameMode\nEnd\n",
                CompiledData = [0x1D, 0x00, 0x00, 0x00],
                CompiledSize = 4,
                IsCompiled = true
            },
            new ScriptRecord
            {
                FormId = 0x00AB1002,
                EditorId = "BravoReconstructedSCRIPT",
                SourceText =
                    "; === Decompiled from captured SCDA — no proven source text in the dump. ===\r\n" +
                    "ScriptName BravoReconstructedSCRIPT\r\nBegin GameMode\r\nEnd\r\n",
                SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                DecompiledText = "ScriptName BravoReconstructedSCRIPT\nBegin GameMode\nEnd\n",
                CompiledData = [0x1D, 0x00, 0x00, 0x00],
                CompiledSize = 4,
                IsCompiled = true
            }
        ];
    }

    private static string[] ReportLines(string report)
    {
        return report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static int CountLines(string[] lines, string line)
    {
        return lines.Count(candidate => string.Equals(candidate, line, StringComparison.Ordinal));
    }

    [Fact]
    public void Script_report_labels_decompiled_substitute_as_reconstructed()
    {
        var files = GeckReportGenerator.GenerateAllReports(
            new ReportDataSources(new RecordCollection { Scripts = DumpScripts() }));

        var lines = ReportLines(files["script_report.txt"]);

        // Counts: only the captured text is source; the reconstruction is counted on its own line.
        Assert.Contains("  With Source (SCTX): 1", lines);
        Assert.Contains("  With Reconstruction: 1", lines);
        Assert.Contains("  With Bytecode (SCDA): 2", lines);

        // Headings: the captured body keeps "Source (SCTX):"; the reconstruction never gets it.
        Assert.Equal(1, CountLines(lines, "Source (SCTX):"));
        Assert.Equal(1, CountLines(lines, "Reconstruction (SCDA):"));
        var bravo = Array.IndexOf(lines, "Editor ID:      BravoReconstructedSCRIPT");
        Assert.True(bravo > 0);
        Assert.Equal(-1, Array.IndexOf(lines, "Source (SCTX):", bravo));
        Assert.True(Array.IndexOf(lines, "Reconstruction (SCDA):", bravo)
                    > bravo);

        // Each dump script says what its text is, right after the "Source:" line.
        var alphaOrigin = Array.IndexOf(lines,
            "Source Origin:  dmp-fragment (captured SCTX; correspondence: accepted)");
        Assert.True(alphaOrigin > 0);
        Assert.Equal("Source:         ESM Record", lines[alphaOrigin - 1]);
        var bravoOrigin = Array.IndexOf(lines,
            "Source Origin:  decompiled-from-bytecode " +
            "(reconstruction)");
        Assert.True(bravoOrigin > bravo);

        // The existing lines stay verbatim, and "Bytecode Order:" still follows "Endianness:" directly.
        Assert.Equal(2, CountLines(lines, "Is Compiled:    True"));
        var endianness = Array.IndexOf(lines, "Endianness:     Little-Endian (PC)", bravo);
        Assert.True(endianness > bravoOrigin);
        Assert.StartsWith("Bytecode Order: ", lines[endianness + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Script_report_for_plugin_scripts_keeps_its_existing_lines()
    {
        var plugin = new ScriptRecord
        {
            FormId = 0x00168CFC,
            EditorId = "VERShadows01QuestScript",
            SourceText = "scn VERShadows01QuestScript\r\nBegin GameMode\r\nEnd"
        };

        var report = GeckScriptWriter.GenerateScriptsReport([plugin]);
        var lines = ReportLines(report);

        Assert.Contains("  With Source (SCTX): 1", lines);
        Assert.Equal(1, CountLines(lines, "Source (SCTX):"));
        Assert.DoesNotContain("With Reconstructed Source", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Source Origin:", report, StringComparison.Ordinal);
        var sourceLine = Array.IndexOf(lines, "Source:         ESM Record");
        Assert.True(sourceLine > 0);
        Assert.Equal("Endianness:     Little-Endian (PC)", lines[sourceLine + 1]);
    }

    [Fact]
    public void Script_report_for_dump_input_names_unattributed_and_absent_source()
    {
        var unattributed = new ScriptRecord
        {
            FormId = 0x00AB1003,
            EditorId = "CharlieUnattributedSCRIPT",
            SourceText = "scn CharlieUnattributedSCRIPT"
        };
        var absent = new ScriptRecord
        {
            FormId = 0x00AB1004,
            EditorId = "DeltaNoSourceSCRIPT",
            CompiledData = [0x1D, 0x00, 0x00, 0x00],
            CompiledSize = 4
        };

        var lines = ReportLines(
            GeckScriptWriter.GenerateScriptsReport([unattributed, absent], null, isMemoryDumpInput: true));

        Assert.Contains("  With Source (SCTX): 1", lines);
        Assert.Contains("Source Origin:  unattributed-same-dump (captured SCTX; correspondence: unverified)", lines);
        Assert.Contains($"Source Origin:  none ({AbsenceWordingLiteral})", lines);
    }

    [Fact]
    public void BuildScriptReport_adds_source_origin_only_for_dump_derived_scripts()
    {
        var plugin = GeckScriptWriter.BuildScriptReport(
            new ScriptRecord
            {
                FormId = 0x00168CFC,
                EditorId = "VERShadows01QuestScript",
                SourceText = "scn VERShadows01QuestScript"
            },
            FormIdResolver.Empty);
        var pluginIdentity = Assert.Single(plugin.Sections, section => section.Name == "Identity");
        Assert.Equal(new[] { "Type", "Is Compiled" }, pluginIdentity.Fields.Select(field => field.Key).ToArray());

        var reconstructed = GeckScriptWriter.BuildScriptReport(DumpScripts()[1], FormIdResolver.Empty);
        var identity = Assert.Single(reconstructed.Sections, section => section.Name == "Identity");
        var origin = Assert.IsType<ReportValue.StringVal>(
            identity.Fields.Single(field => field.Key == "Source Origin").Value);
        Assert.Equal(
            "decompiled-from-bytecode (reconstruction)",
            origin.Raw);
        // The body keeps the section name "Source" so comparisons still align.
        Assert.Single(reconstructed.Sections, section => section.Name == "Source");

        var absent = GeckScriptWriter.BuildScriptReport(
            new ScriptRecord { FormId = 0x00AB1004, EditorId = "DeltaNoSourceSCRIPT" },
            FormIdResolver.Empty,
            isMemoryDumpInput: true);
        var absentIdentity = Assert.Single(absent.Sections, section => section.Name == "Identity");
        Assert.Equal($"none ({AbsenceWordingLiteral})",
            absentIdentity.Fields.Single(field => field.Key == "Source Origin").Value.Display);
    }

    [Theory]
    [InlineData(ScriptSourceTextOrigin.DecompiledFromBytecode, true, ReconstructionLabelLiteral)]
    [InlineData(ScriptSourceTextOrigin.RuntimeSameObject, true,
        "Recovered source (runtime object)")]
    [InlineData(ScriptSourceTextOrigin.None, false, "Source (SCTX)")]
    public void Quest_show_labels_its_script_text_by_provenance(ScriptSourceTextOrigin origin, bool dumpInput,
        string expectedLabel)
    {
        const uint questFormId = 0x00AB2001;
        const uint scriptFormId = 0x00AB2002;
        var records = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = questFormId, EditorId = "LabelProbeQuest", Script = scriptFormId }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = scriptFormId,
                    EditorId = "LabelProbeQuestSCRIPT",
                    SourceText = "scn LabelProbeQuestSCRIPT\r\nBegin GameMode\r\nEnd",
                    SourceTextOrigin = origin,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
                }
            ]
        };

        var rendered = false;
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 400;
            rendered = new QuestShowRenderer().TryShow(records, FormIdResolver.Empty, questFormId, null,
                new ShowRenderContext(console, false, dumpInput));
        });

        Assert.True(rendered);
        Assert.Contains($"{expectedLabel}:", output, StringComparison.Ordinal);
        if (dumpInput)
        {
            Assert.DoesNotContain("Source (SCTX):", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dmp_scripts_list_counts_reconstructions_apart_from_captured_source_and_escapes_editor_ids()
    {
        var scripts = DumpScripts();
        scripts.Add(new ScriptRecord
        {
            FormId = 0x00AB1005,
            EditorId = "Odd[Name]SCRIPT",
            CompiledData = [0x1D, 0x00, 0x00, 0x00],
            CompiledSize = 4
        });

        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 400;
            DmpScriptCommands.RenderScriptList(console, scripts);
        });

        Assert.Contains("With source (SCTX): 1", output, StringComparison.Ordinal);
        Assert.Contains("With reconstructed source (decompiled from SCDA): 1", output, StringComparison.Ordinal);
        Assert.Contains($"Without source text: 1 ({AbsenceWordingLiteral})", output, StringComparison.Ordinal);
        // Only the captured text is comparable with its decompilation; the reconstruction IS the decompilation.
        Assert.Contains("With both (comparable): 1", output, StringComparison.Ordinal);
        Assert.Contains("With bytecode (SCDA): 3", output, StringComparison.Ordinal);
        Assert.Contains("Odd[Name]SCRIPT", output, StringComparison.Ordinal);
        Assert.Contains("decompiled-from-bytecode", output, StringComparison.Ordinal);
        Assert.Contains("dmp-fragment", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Dmp_scripts_show_heads_a_reconstruction_as_one()
    {
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 400;
            DmpScriptCommands.RenderScriptDetail(console, DumpScripts()[1]);
        });

        Assert.Contains($"--- {ReconstructionLabelLiteral} ---", output, StringComparison.Ordinal);
        Assert.Contains(
            "Source Origin: decompiled-from-bytecode " +
            "(reconstruction)",
            output, StringComparison.Ordinal);
        Assert.Contains(
            "--- Reconstruction (SCDA) ---",
            output, StringComparison.Ordinal);
        Assert.DoesNotContain("--- Source (SCTX) ---", output, StringComparison.Ordinal);
    }

    #endregion
}
