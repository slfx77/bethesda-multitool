using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Semantic;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Semantic;

public sealed class EsmLoadOrderAndRebaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "esm-load-order-tests", Guid.NewGuid().ToString("N"));

    public EsmLoadOrderAndRebaseTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task ResolveDirectoryAsync_orders_masters_before_dependents()
    {
        var zeta = WriteHeaderOnlyEsm("Zeta.esm", "Fallout3.esm");
        var fallout3 = WriteHeaderOnlyEsm("Fallout3.esm");
        var anchorage = WriteHeaderOnlyEsm("Anchorage.esm", "Fallout3.esm");

        var ordered = await EsmLoadOrderResolver.ResolveDirectoryAsync(
            _tempDir,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [fallout3, anchorage, zeta],
            ordered.Select(file => file.FilePath).ToList());
    }

    [Fact]
    public void Mapper_flattens_plugin_local_and_master_formids_to_base_formids()
    {
        var descriptor = new EsmLoadOrderFile(
            "Anchorage.esm",
            "Anchorage.esm",
            new EsmFileHeader { Masters = ["Fallout3.esm"] },
            LoadIndex: 1);
        var mapper = new EsmFormIdLoadOrderMapper(
            descriptor,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fallout3.esm"] = 0,
                ["Anchorage.esm"] = 1
            },
            flattenToBase: true);

        Assert.Equal(0x00092BDCu, mapper.Map(0x01092BDCu));
        Assert.Equal(0x00012345u, mapper.Map(0x00012345u));
    }

    [Fact]
    public void RecordCollectionFormIdRebaser_rebases_records_references_and_indexes()
    {
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x01092BDC,
                    EditorId = "DLC01WeapSteelSaw",
                    AmmoFormId = 0x01000100,
                    ProjectileFormId = 0x01000200,
                    ModSlots = [new WeaponModSlot { SlotIndex = 1, ModFormId = 0x01000300 }]
                }
            ],
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x01001000,
                    EditorId = "DLC01Cell",
                    WorldspaceFormId = 0x01002000,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x01003000,
                            BaseFormId = 0x01092BDC,
                            LockKeyFormId = 0x01004000,
                            LinkedRefChildrenFormIds = [0x01005000]
                        }
                    ]
                }
            ],
            FormLists =
            [
                new FormListRecord
                {
                    FormId = 0x01006000,
                    FormIds = [0x01092BDC, 0x01000100]
                }
            ],
            FormIdToEditorId = new Dictionary<uint, string>
            {
                [0x01092BDC] = "DLC01WeapSteelSaw"
            },
            FormIdToDisplayName = new Dictionary<uint, string>
            {
                [0x01092BDC] = "Auto Axe"
            },
            ModelPathIndex = new Dictionary<uint, string>
            {
                [0x01092BDC] = "weapons\\steelSaw.nif"
            }
        };

        var rebased = RecordCollectionFormIdRebaser.Rebase(records, formId => formId & 0x00FFFFFFu);

        var weapon = Assert.Single(rebased.Weapons);
        Assert.Equal(0x00092BDCu, weapon.FormId);
        Assert.Equal(0x00000100u, weapon.AmmoFormId);
        Assert.Equal(0x00000200u, weapon.ProjectileFormId);
        Assert.Equal(0x00000300u, Assert.Single(weapon.ModSlots).ModFormId);

        var cell = Assert.Single(rebased.Cells);
        Assert.Equal(0x00001000u, cell.FormId);
        Assert.Equal(0x00002000u, cell.WorldspaceFormId);
        var placed = Assert.Single(cell.PlacedObjects);
        Assert.Equal(0x00003000u, placed.FormId);
        Assert.Equal(0x00092BDCu, placed.BaseFormId);
        Assert.Equal(0x00004000u, placed.LockKeyFormId);
        Assert.Equal([0x00005000u], placed.LinkedRefChildrenFormIds);

        Assert.Equal(0x00006000u, Assert.Single(rebased.FormLists).FormId);
        Assert.Equal([0x00092BDCu, 0x00000100u], Assert.Single(rebased.FormLists).FormIds);
        Assert.True(rebased.FormIdToEditorId.ContainsKey(0x00092BDCu));
        Assert.True(rebased.FormIdToDisplayName.ContainsKey(0x00092BDCu));
        Assert.True(rebased.ModelPathIndex.ContainsKey(0x00092BDCu));
    }

    [Fact]
    public void Rebased_dlc_steel_saw_aggregates_into_base_formid_row()
    {
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x01092BDC,
                    EditorId = "DLC01WeapSteelSaw",
                    FullName = "Auto Axe"
                }
            ],
            FormIdToEditorId = new Dictionary<uint, string>
            {
                [0x01092BDC] = "DLC01WeapSteelSaw"
            },
            FormIdToDisplayName = new Dictionary<uint, string>
            {
                [0x01092BDC] = "Auto Axe"
            }
        };
        var rebased = RecordCollectionFormIdRebaser.Rebase(records, formId => formId & 0x00FFFFFFu);
        var filePath = WriteHeaderOnlyEsm("Fallout3.base.esm");

        var index = CrossDumpAggregator.Aggregate(
            [(filePath, rebased, rebased.CreateResolver(), null)]);

        var weapons = index.StructuredRecords["Weapon"];
        Assert.Contains(0x00092BDCu, weapons.Keys);
        Assert.DoesNotContain(0x01092BDCu, weapons.Keys);
    }

    [Fact]
    public void CrossDumpAggregator_upgrades_virtual_exterior_cell_to_unique_real_coordinate_row()
    {
        var resolver = BuildCellResolver();
        var realRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00005000,
                    EditorId = "RealCell",
                    FullName = "Real Cell",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010
                }
            ]
        };
        var virtualRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFF000001,
                    EditorId = "[Virtual 4,-2 WastelandNV]",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010,
                    IsVirtual = true
                }
            ]
        };

        var index = CrossDumpAggregator.Aggregate(
            [
                ("real.dmp", realRecords, resolver, null),
                ("virtual.dmp", virtualRecords, resolver, null)
            ]);

        var cells = index.StructuredRecords["Cell"];
        Assert.Contains(0x00005000u, cells.Keys);
        Assert.DoesNotContain(0xFF000001u, cells.Keys);
        Assert.Equal([0, 1], cells[0x00005000].Keys.OrderBy(key => key).ToArray());
        Assert.Equal("RealCell", cells[0x00005000][1].EditorId);
        Assert.Equal("Real Cell", cells[0x00005000][1].DisplayName);
        var virtualDumpFormIdField = Assert.IsType<ReportValue.StringVal>(
            cells[0x00005000][1].Sections.Single(section => section.Name == "Identity")
                .Fields.Single(field => field.Key == "FormID").Value);
        var virtualDumpEditorIdField = Assert.IsType<ReportValue.StringVal>(
            cells[0x00005000][1].Sections.Single(section => section.Name == "Identity")
                .Fields.Single(field => field.Key == "Editor ID").Value);
        var virtualDumpDisplayNameField = Assert.IsType<ReportValue.StringVal>(
            cells[0x00005000][1].Sections.Single(section => section.Name == "Identity")
                .Fields.Single(field => field.Key == "Display Name").Value);
        Assert.Equal("0x00005000", virtualDumpFormIdField.Raw);
        Assert.Equal("RealCell", virtualDumpEditorIdField.Raw);
        Assert.Equal("Real Cell", virtualDumpDisplayNameField.Raw);
        Assert.DoesNotContain("[Virtual", cells[0x00005000][1].EditorId);
        Assert.Equal((4, -2), index.CellGridCoords[0x00005000]);
        Assert.Equal(
            "0xFF000001",
            index.RecordMetadata["Cell"][0x00005000]["upgradedVirtualFormIds"]);
        Assert.Equal(
            "1:0xFF000001",
            index.RecordMetadata["Cell"][0x00005000]["upgradedVirtualFormIdsByDump"]);

        var json = ComparisonJsonBlobBuilder.Build(
            cells,
            index.Dumps,
            "Cell",
            index.RecordGroups["Cell"],
            null,
            null,
            index.RecordMetadata["Cell"],
            index.CellGridCoords);
        using var document = JsonDocument.Parse(json);
        var recordJson = document.RootElement.GetProperty("records").GetProperty("0x00005000");
        Assert.Equal("RealCell", recordJson.GetProperty("editorId").GetString());
        Assert.Equal("Real Cell", recordJson.GetProperty("displayName").GetString());
        Assert.False(recordJson.TryGetProperty("editorIdHistory", out _));
        Assert.False(recordJson.TryGetProperty("nameHistory", out _));
        Assert.DoesNotContain("[Virtual", json);
    }

    [Fact]
    public void CrossDumpAggregator_keeps_virtual_cell_separate_when_coordinate_match_is_ambiguous()
    {
        var resolver = BuildCellResolver();
        var realRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00005000,
                    EditorId = "RealCellA",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010
                },
                new CellRecord
                {
                    FormId = 0x00005001,
                    EditorId = "RealCellB",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010
                }
            ]
        };
        var virtualRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFF000001,
                    EditorId = "[Virtual 4,-2 WastelandNV]",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010,
                    IsVirtual = true
                }
            ]
        };

        var index = CrossDumpAggregator.Aggregate(
            [
                ("real.dmp", realRecords, resolver, null),
                ("virtual.dmp", virtualRecords, resolver, null)
            ]);

        var cells = index.StructuredRecords["Cell"];
        Assert.Contains(0x00005000u, cells.Keys);
        Assert.Contains(0x00005001u, cells.Keys);
        Assert.DoesNotContain(0xFF000001u, cells.Keys);
        var syntheticVirtualKey = cells.Keys.Single(key => key != 0x00005000u && key != 0x00005001u);
        Assert.Equal(0xFD000001u, syntheticVirtualKey);
        Assert.Single(cells[syntheticVirtualKey]);
        Assert.Null(cells[syntheticVirtualKey][1].EditorId);
        Assert.False(index.RecordMetadata.TryGetValue("Cell", out var metadata) &&
                     metadata.ContainsKey(0x00005000));
    }

    [Fact]
    public void CrossDumpAggregator_aligns_virtual_only_exterior_cells_by_coordinate()
    {
        var resolver = BuildCellResolver();
        var firstDump = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFF000001,
                    EditorId = "[Virtual 4,-2 WastelandNV]",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010,
                    IsVirtual = true
                }
            ]
        };
        var secondDump = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFE800123,
                    EditorId = "[Virtual 4,-2 WastelandNV]",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010,
                    IsVirtual = true
                }
            ]
        };

        var index = CrossDumpAggregator.Aggregate(
            [
                ("first.dmp", firstDump, resolver, null),
                ("second.dmp", secondDump, resolver, null)
            ]);

        var cells = index.StructuredRecords["Cell"];
        Assert.DoesNotContain(0xFF000001u, cells.Keys);
        Assert.DoesNotContain(0xFE800123u, cells.Keys);
        var syntheticKey = Assert.Single(cells.Keys);
        Assert.Equal(0xFD000001u, syntheticKey);
        Assert.Equal([0, 1], cells[syntheticKey].Keys.OrderBy(key => key).ToArray());
        Assert.Null(cells[syntheticKey][0].EditorId);
        Assert.Null(cells[syntheticKey][1].EditorId);
        Assert.Equal((4, -2), index.CellGridCoords[syntheticKey]);

        var json = ComparisonJsonBlobBuilder.Build(
            cells,
            index.Dumps,
            "Cell",
            index.RecordGroups["Cell"],
            null,
            null,
            index.RecordMetadata["Cell"],
            index.CellGridCoords);
        Assert.DoesNotContain("[Virtual", json);
    }

    [Fact]
    public void CrossDumpAggregator_keeps_virtual_cells_without_worldspace_or_coordinates_separate()
    {
        var resolver = BuildCellResolver();
        var realRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00005000,
                    EditorId = "RealCell",
                    GridX = 4,
                    GridY = -2,
                    WorldspaceFormId = 0x00000010
                }
            ]
        };
        var missingWorldspaceRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFF000001,
                    EditorId = "[Virtual 4,-2 Unknown]",
                    GridX = 4,
                    GridY = -2,
                    IsVirtual = true
                }
            ]
        };
        var missingCoordinateRecords = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFF000002,
                    EditorId = "[Virtual Unknown WastelandNV]",
                    WorldspaceFormId = 0x00000010,
                    IsVirtual = true
                }
            ]
        };

        var index = CrossDumpAggregator.Aggregate(
            [
                ("real.dmp", realRecords, resolver, null),
                ("missing-worldspace.dmp", missingWorldspaceRecords, resolver, null),
                ("missing-coordinate.dmp", missingCoordinateRecords, resolver, null)
            ]);

        var cells = index.StructuredRecords["Cell"];
        Assert.Contains(0x00005000u, cells.Keys);
        Assert.Contains(0xFF000001u, cells.Keys);
        Assert.Contains(0xFF000002u, cells.Keys);
    }

    private static FormIdResolver BuildCellResolver()
    {
        return new FormIdResolver(
            new Dictionary<uint, string>
            {
                [0x00000010] = "WastelandNV",
                [0x00005000] = "RealCell",
                [0x00005001] = "RealCellB"
            },
            new Dictionary<uint, string>
            {
                [0x00000010] = "Mojave Wasteland",
                [0x00005000] = "Real Cell",
                [0x00005001] = "Real Cell B"
            },
            []);
    }

    private string WriteHeaderOnlyEsm(string fileName, params string[] masters)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, BuildHeaderOnlyEsm(masters));
        return path;
    }

    private static byte[] BuildHeaderOnlyEsm(params string[] masters)
    {
        var subrecords = new List<(string Signature, byte[] Data)>
        {
            ("HEDR", BuildHedr())
        };
        foreach (var master in masters)
        {
            subrecords.Add(("MAST", Encoding.ASCII.GetBytes(master + "\0")));
            subrecords.Add(("DATA", new byte[8]));
        }

        var dataSize = subrecords.Sum(subrecord => 6 + subrecord.Data.Length);
        var data = new byte[24 + dataSize];
        Encoding.ASCII.GetBytes("TES4", data.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);

        var offset = 24;
        foreach (var (signature, bytes) in subrecords)
        {
            Encoding.ASCII.GetBytes(signature, data.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4), (ushort)bytes.Length);
            bytes.CopyTo(data.AsSpan(offset + 6));
            offset += 6 + bytes.Length;
        }

        return data;
    }

    private static byte[] BuildHedr()
    {
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8), 0x800);
        return hedr;
    }
}
