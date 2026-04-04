using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using FalloutXbox360Utils.Core.Strings;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export;

/// <summary>
///     Smoke tests for GeckReportGenerator, CsvActorWriter, and CsvItemWriter.
///     These tests anchor behavior before the partial class elimination refactoring.
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
}