using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export;

public class ComparisonHtmlRegressionTests
{
    [Fact]
    public void ComparisonJson_IncludesDialoguePromptAndResponseSearchText()
    {
        var report = new RecordReport(
            "Dialogue",
            0x00001234,
            "TestInfo",
            null,
            [
                new ReportSection("Prompt",
                [
                    new ReportField("Player", ReportValue.String("\"Where is Primm?\""))
                ]),
                new ReportSection("Responses",
                [
                    new ReportField("Response 0", ReportValue.String("\"Primm is down the road.\""))
                ])
            ]);
        var formIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [report.FormId] = new() { [0] = report }
        };
        var dumps = new List<DumpSnapshot>
        {
            new("test.dmp", DateTime.UnixEpoch, "test", true)
        };

        var json = ComparisonJsonBlobBuilder.Build(
            formIdMap,
            dumps,
            "Dialogue",
            groups: null,
            alternateGroups: null,
            defaultGroupMode: null,
            metadata: null,
            cellGridCoords: null);

        using var doc = JsonDocument.Parse(json);
        var searchText = doc.RootElement
            .GetProperty("records")
            .GetProperty("0x00001234")
            .GetProperty("searchText")
            .GetString();

        Assert.Contains("Where is Primm?", searchText);
        Assert.Contains("Primm is down the road.", searchText);
    }

    [Fact]
    public void ComparisonJson_IncludesDialogTopicChildDialogueSearchText()
    {
        var report = new RecordReport(
            "DialogTopic",
            0x00002222,
            "VDialogueTest",
            "Test Topic",
            [
                new ReportSection("Prompt",
                [
                    new ReportField("Player", ReportValue.String("\"Topic prompt\""))
                ])
            ]);
        var formIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>
        {
            [report.FormId] = new() { [0] = report }
        };
        var dumps = new List<DumpSnapshot>
        {
            new("test.dmp", DateTime.UnixEpoch, "test", true)
        };
        var metadata = new Dictionary<uint, Dictionary<string, string>>
        {
            [report.FormId] = new()
            {
                ["searchText"] = "Hidden child response line"
            }
        };

        var json = ComparisonJsonBlobBuilder.Build(
            formIdMap,
            dumps,
            "DialogTopic",
            groups: null,
            alternateGroups: null,
            defaultGroupMode: null,
            metadata: metadata,
            cellGridCoords: null);

        using var doc = JsonDocument.Parse(json);
        var searchText = doc.RootElement
            .GetProperty("records")
            .GetProperty("0x00002222")
            .GetProperty("searchText")
            .GetString();

        Assert.Contains("Topic prompt", searchText);
        Assert.Contains("Hidden child response line", searchText);
    }

    [Fact]
    public void ComparisonScript_KeysSpellEffectsByEffectIdentity()
    {
        Assert.Contains("fieldMap.Effect", ComparisonJsRenderer.Script);
        Assert.Contains("'Effect=' + scalarKeyText(fieldMap.Effect)", ComparisonJsRenderer.Script);
    }

    [Fact]
    public void ComparisonStyles_KeepIdentityColumnsContentSizedAndVisibleBuildColumnsEqual()
    {
        Assert.Contains(".col-editor", ComparisonCssStyles.Styles, StringComparison.Ordinal);
        Assert.Contains("width: 1px;", ComparisonCssStyles.Styles, StringComparison.Ordinal);
        Assert.Contains(".build-cell", ComparisonCssStyles.Styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--build-col-width, auto);", ComparisonCssStyles.Styles,
            StringComparison.Ordinal);
        Assert.Contains("min-width: var(--build-col-width, 0);", ComparisonCssStyles.Styles,
            StringComparison.Ordinal);

        Assert.Contains("function equalizeVisibleBuildColumns()", ComparisonJsRenderer.Script,
            StringComparison.Ordinal);
        Assert.Contains("thead th:not(.build-header)", ComparisonJsRenderer.Script,
            StringComparison.Ordinal);
        Assert.Contains("table.style.setProperty('--build-col-width'", ComparisonJsRenderer.Script,
            StringComparison.Ordinal);
        Assert.Contains("build-header build-cell build-col-", ComparisonJsRenderer.Script,
            StringComparison.Ordinal);
        Assert.Contains("build-cell build-col-' + di", ComparisonJsRenderer.Script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonScript_KeysDomainListsByStableIdentityFields()
    {
        var script = ComparisonJsRenderer.Script;

        Assert.Contains("return 'Faction=' + scalarKeyText(fieldMap.Faction);", script,
            StringComparison.Ordinal);
        Assert.Contains("return 'Effect=' + scalarKeyText(fieldMap.Effect)", script,
            StringComparison.Ordinal);
        Assert.Contains("+ '|Target=' + scalarKeyText(fieldMap.Target);", script,
            StringComparison.Ordinal);
        Assert.Contains("return 'PerkEntry=Rank=' + scalarKeyText(fieldMap.Rank)", script,
            StringComparison.Ordinal);
        Assert.Contains("+ '|EntryPoint=' + scalarKeyText(fieldMap['Entry Point'])", script,
            StringComparison.Ordinal);
        Assert.Contains("+ '|EffectForm=' + scalarKeyText(fieldMap['Effect Form']);", script,
            StringComparison.Ordinal);
        Assert.Contains("return 'PlacedFormID=' + scalarKeyText(fieldMap.FormID);", script,
            StringComparison.Ordinal);
        Assert.Contains("return 'Placed=' + scalarKeyText(fieldMap.Base)", script,
            StringComparison.Ordinal);
        Assert.Contains("+ '|Cell=' + scalarKeyText(fieldMap.Cell || fieldMap['Containing Cell'])", script,
            StringComparison.Ordinal);
        Assert.Contains("var namedItemKey = firstNonEmptyScalarKey(", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void KeyLockedDoorIndex_CanonicalizesVirtualCellToRealCell()
    {
        var records = new RecordCollection
        {
            Keys =
            [
                new KeyRecord { FormId = 0x00001000, EditorId = "TestKey" }
            ],
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00002000,
                    EditorId = "RealCell",
                    WorldspaceFormId = 0x0000003C,
                    GridX = 4,
                    GridY = -2
                },
                new CellRecord
                {
                    FormId = 0xFE000001,
                    IsVirtual = true,
                    WorldspaceFormId = 0x0000003C,
                    GridX = 4,
                    GridY = -2,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x00003000,
                            BaseFormId = 0x00004000,
                            RecordType = "REFR",
                            LockKeyFormId = 0x00001000
                        }
                    ]
                }
            ]
        };

        var indexes = CrossDumpAggregator.BuildKeyLockedDoorIndexes([("test.dmp", records)]);
        var keyEntries = indexes["test.dmp"][0x00001000];

        var entry = Assert.Single(keyEntries);
        Assert.Equal(0x00002000u, entry.CellFormId);
        Assert.Equal(0x00003000u, entry.Ref.FormId);
    }

    [Fact]
    public void ContainerPlacementIndex_UsesPlacedRefrsWithContainerBaseAndCanonicalizesVirtualCell()
    {
        var records = new RecordCollection
        {
            Containers =
            [
                new ContainerRecord { FormId = 0x00001000, EditorId = "TestContainer" }
            ],
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00002000,
                    EditorId = "RealCell",
                    FullName = "Real Cell",
                    WorldspaceFormId = 0x0000003C,
                    GridX = 4,
                    GridY = -2
                },
                new CellRecord
                {
                    FormId = 0xFE000001,
                    IsVirtual = true,
                    WorldspaceFormId = 0x0000003C,
                    GridX = 4,
                    GridY = -2,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x00003000,
                            BaseFormId = 0x00001000,
                            BaseEditorId = "TestContainer",
                            EditorId = "PlacedTestContainer",
                            RecordType = "REFR",
                            X = 1,
                            Y = 2,
                            Z = 3
                        },
                        new PlacedReference
                        {
                            FormId = 0x00003010,
                            BaseFormId = 0x00001000,
                            RecordType = "ACHR"
                        }
                    ]
                }
            ]
        };

        var indexes = CrossDumpAggregator.BuildContainerPlacementIndexes([("test.dmp", records)]);
        var entries = indexes["test.dmp"][0x00001000];

        var entry = Assert.Single(entries);
        Assert.Equal(0x00002000u, entry.CellFormId);
        Assert.Equal("RealCell", entry.CellEditorId);
        Assert.Equal("Real Cell", entry.CellName);
        Assert.Equal(0x00003000u, entry.Ref.FormId);
    }

    [Fact]
    public void CellReport_CanonicalizesDoorLinksAndKeepsDoorRotation()
    {
        var sourceDoor = new PlacedReference
        {
            FormId = 0x00003000,
            BaseFormId = 0x00004000,
            BaseEditorId = "SourceDoorBase",
            RecordType = "REFR",
            X = 1,
            Y = 2,
            Z = 3,
            RotZ = 3.142f,
            DestinationCellFormId = 0xFE800123,
            DestinationDoorFormId = 0x00005000
        };
        var destinationDoor = new PlacedReference
        {
            FormId = 0x00005000,
            BaseFormId = 0x00006000,
            BaseEditorId = "DestinationDoorBase",
            RecordType = "REFR"
        };
        var cell = new CellRecord
        {
            FormId = 0x00002000,
            EditorId = "CurrentCell",
            PlacedObjects = [sourceDoor]
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00002000] = "CurrentCell",
                [0x00004000] = "SourceDoorBase",
                [0x00005000] = "DestinationDoorRef",
                [0x00006000] = "DestinationDoorBase",
                [0x00007000] = "RealDestinationCell"
            },
            new Dictionary<uint, string>
            {
                [0x00007000] = "Real Destination Cell"
            },
            new Dictionary<uint, uint>
            {
                [0x00003000] = 0x00004000,
                [0x00005000] = 0x00006000
            });
        var locations = new Dictionary<uint, PlacedReferenceLocation>
        {
            [0x00005000] = new(destinationDoor, 0x00007000)
        };

        var report = GeckWorldWriter.BuildCellReport(cell, resolver, locations);
        var doorList = Assert.IsType<ReportValue.ListVal>(
            report.Sections.Single(section => section.Name == "Door Links")
                .Fields.Single(field => field.Key == "Linked Doors").Value);
        var door = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(doorList.Items));
        var fields = door.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(0x00007000u, Assert.IsType<ReportValue.FormIdVal>(fields["Links to"]).Raw);
        Assert.Equal(0x00005000u, Assert.IsType<ReportValue.FormIdVal>(fields["Destination Door"]).Raw);
        Assert.Contains("DestinationDoorBase", fields["Destination Door"].Display, StringComparison.Ordinal);
        Assert.Equal("(0.000, 0.000, 3.142)", Assert.IsType<ReportValue.StringVal>(fields["Rotation"]).Raw);
        Assert.DoesNotContain("FE800123", door.Display, StringComparison.OrdinalIgnoreCase);

        var placedList = Assert.IsType<ReportValue.ListVal>(
            report.Sections.Single(section => section.Name == "Placed Objects")
                .Fields.Single(field => field.Key == "Objects").Value);
        var placedDoor = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(placedList.Items));
        var placedFields = placedDoor.Fields.ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(0x00007000u, Assert.IsType<ReportValue.FormIdVal>(placedFields["Links to"]).Raw);
        Assert.DoesNotContain("FE800123", placedDoor.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aggregate_ContainerReportIncludesPrebuiltPlacementLocations()
    {
        const string filePath = "test.dmp";
        const uint containerFormId = 0x00001000;
        const uint cellFormId = 0x00002000;
        const uint refFormId = 0x00003000;
        const uint worldspaceFormId = 0x0000003C;
        const uint originCellFormId = 0x00004000;

        var records = new RecordCollection
        {
            Containers =
            [
                new ContainerRecord { FormId = containerFormId, EditorId = "TestContainer" }
            ],
            Cells =
            [
                new CellRecord
                {
                    FormId = cellFormId,
                    EditorId = "RealCell",
                    FullName = "Real Cell",
                    WorldspaceFormId = worldspaceFormId,
                    GridX = 4,
                    GridY = -2,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = refFormId,
                            BaseFormId = containerFormId,
                            BaseEditorId = "TestContainer",
                            EditorId = "PlacedTestContainer",
                            RecordType = "REFR",
                            X = 1,
                            Y = 2,
                            Z = 3,
                            RotZ = 0.5f,
                            IsPersistent = true,
                            OriginCellFormId = originCellFormId,
                            AssignmentSource = "PersistentRedistributed"
                        }
                    ]
                }
            ]
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>
            {
                [containerFormId] = "TestContainer",
                [refFormId] = "PlacedTestContainer",
                [cellFormId] = "RealCell",
                [worldspaceFormId] = "WastelandNV",
                [originCellFormId] = "PersistentCell"
            },
            new Dictionary<uint, string>
            {
                [containerFormId] = "Test Container",
                [cellFormId] = "Real Cell"
            },
            new Dictionary<uint, uint> { [refFormId] = containerFormId });

        var placementIndexes = CrossDumpAggregator.BuildContainerPlacementIndexes([(filePath, records)]);
        records.Cells.Clear();
        var index = CrossDumpAggregator.Aggregate(
            [(filePath, records, resolver, null)],
            new HashSet<string>(["Container"], StringComparer.OrdinalIgnoreCase),
            containerPlacementIndexes: placementIndexes);

        var report = index.StructuredRecords["Container"][containerFormId][0];
        var section = Assert.Single(report.Sections, section => section.Name == "Placements");
        var list = Assert.IsType<ReportValue.ListVal>(
            section.Fields.Single(field => field.Key == "References").Value);
        var placement = Assert.IsType<ReportValue.CompositeVal>(Assert.Single(list.Items));
        var fields = placement.Fields.ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(refFormId, Assert.IsType<ReportValue.FormIdVal>(fields["FormID"]).Raw);
        Assert.Equal(cellFormId, Assert.IsType<ReportValue.FormIdVal>(fields["Cell"]).Raw);
        Assert.Equal(worldspaceFormId, Assert.IsType<ReportValue.FormIdVal>(fields["Worldspace"]).Raw);
        Assert.Equal("4, -2", Assert.IsType<ReportValue.StringVal>(fields["Grid"]).Raw);
        Assert.Equal("(1.0, 2.0, 3.0)", Assert.IsType<ReportValue.StringVal>(fields["Position"]).Raw);
        Assert.Equal("(0.000, 0.000, 0.500)", Assert.IsType<ReportValue.StringVal>(fields["Rotation"]).Raw);
        Assert.True(Assert.IsType<ReportValue.BoolVal>(fields["Persistent"]).Raw);
        Assert.Equal(originCellFormId, Assert.IsType<ReportValue.FormIdVal>(fields["Origin Cell"]).Raw);
        Assert.Equal("PersistentRedistributed",
            Assert.IsType<ReportValue.StringVal>(fields["Assignment Source"]).Raw);
        Assert.Contains("Real Cell", placement.Display, StringComparison.Ordinal);
    }
}
