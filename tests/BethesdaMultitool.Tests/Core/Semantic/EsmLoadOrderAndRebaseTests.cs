using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Comparison;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic;

public sealed class EsmLoadOrderAndRebaseTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "esm-load-order-tests", Guid.NewGuid().ToString("N"));

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
    public async Task ResolveDirectoryAsync_resolves_tes3_headers_and_masters()
    {
        // Regression: EsmParser.ParseFileHeader is TES4-only, so a Morrowind Data Files directory
        // resolved EMPTY ("No ESM/ESP sources found") — every TES3 header read returned null.
        var bloodmoon = WriteTes3Esm("Bloodmoon.esm", "Morrowind.esm");
        var morrowind = WriteTes3Esm("Morrowind.esm");
        var tribunal = WriteTes3Esm("Tribunal.esm", "Morrowind.esm");

        var ordered = await EsmLoadOrderResolver.ResolveDirectoryAsync(
            _tempDir,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [morrowind, bloodmoon, tribunal],
            ordered.Select(file => file.FilePath).ToList());
        var bloodmoonHeader = ordered.Single(f => f.FileName == "Bloodmoon.esm").Header;
        Assert.Equal(["Morrowind.esm"], bloodmoonHeader.Masters);
        Assert.Equal(1.3f, bloodmoonHeader.Version, 2);
    }

    [Fact]
    public void Mapper_flattens_plugin_local_and_master_formids_to_base_formids()
    {
        var descriptor = new EsmLoadOrderFile(
            "Anchorage.esm",
            "Anchorage.esm",
            new EsmFileHeader { Masters = ["Fallout3.esm"] },
            1);
        var mapper = new EsmFormIdLoadOrderMapper(
            descriptor,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fallout3.esm"] = 0,
                ["Anchorage.esm"] = 1
            },
            true);

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
    public void RecordCollectionFormIdRebaser_rebases_starfield_climate_wslt_formids()
    {
        var records = new RecordCollection
        {
            Climate =
            [
                new ClimateRecord
                {
                    FormId = 0x01001000,
                    EditorId = "StarfieldClimate",
                    // Use a List at this IReadOnlyList boundary, matching the CLMT parser's concrete
                    // result and exercising the rebaser's immutable WSLT-entry clone path.
                    WeatherSettingsTypes = new List<ClimateWeatherSettingsEntry>
                    {
                        new(0x0102B544, 75, 0x01000ABC)
                    }
                }
            ]
        };

        var rebased = RecordCollectionFormIdRebaser.Rebase(records, formId => formId & 0x00FFFFFFu);

        var entry = Assert.Single(Assert.Single(rebased.Climate).WeatherSettingsTypes);
        Assert.Equal(0x0002B544u, entry.WeatherSettingsFormId);
        Assert.Equal(75, entry.Chance);
        Assert.Equal(0x00000ABCu, entry.GlobalFormId);

        var original = Assert.Single(Assert.Single(records.Climate).WeatherSettingsTypes);
        Assert.Equal(0x0102B544u, original.WeatherSettingsFormId);
        Assert.Equal(0x01000ABCu, original.GlobalFormId);
    }

    [Fact]
    public void RecordCollectionFormIdRebaser_rebases_starfield_cloud_card_and_preserves_record_envelopes()
    {
        var sourceLayers = new List<StarfieldCloudLayer>();
        var sourcePlanes = new List<StarfieldCloudPlane>();
        var full = new StarfieldCloudFormRecord
        {
            FormId = 0x0100_1000u,
            EditorId = "AuthoredCloud",
            Definition = CreateCloudDefinition(0x0100_2000u, sourceLayers, sourcePlanes),
            Offset = 0x1234,
            IsBigEndian = false
        };
        var noSequence = new StarfieldCloudFormRecord
        {
            FormId = 0x0100_1001u,
            EditorId = "CloudWithoutCardSequence",
            Definition = CreateCloudDefinition(0),
            Offset = 0x2345,
            IsBigEndian = false
        };
        var malformed = new StarfieldCloudFormRecord
        {
            FormId = 0x0100_1002u,
            EditorId = "MalformedCloud",
            Definition = null,
            DecodeFailure = "CLDF REFL schema mismatch",
            Offset = 0x3456,
            IsBigEndian = true
        };
        var records = new RecordCollection { CloudForms = [full, noSequence, malformed] };
        var mappedFormIds = new List<uint>();

        var rebased = RecordCollectionFormIdRebaser.Rebase(records, formId =>
        {
            mappedFormIds.Add(formId);
            return formId & 0x00FF_FFFFu;
        });

        var rebasedFull = rebased.CloudForms.Single(record => record.EditorId == full.EditorId);
        Assert.Equal(0x0000_1000u, rebasedFull.FormId);
        var rebasedDefinition = Assert.IsType<StarfieldCloudFormDefinition>(rebasedFull.Definition);
        Assert.Equal(0x0000_2000u, rebasedDefinition.CloudCardSequenceFormId);
        Assert.Null(rebasedFull.DecodeFailure);
        Assert.Equal(0x1234L, rebasedFull.Offset);
        Assert.False(rebasedFull.IsBigEndian);
        Assert.NotSame(full, rebasedFull);
        Assert.NotSame(full.Definition, rebasedFull.Definition);
        Assert.NotSame(sourceLayers, rebasedDefinition.Layers);
        Assert.NotSame(sourcePlanes, rebasedDefinition.Planes);
        Assert.Empty(rebasedDefinition.Layers);
        Assert.Empty(rebasedDefinition.Planes);

        var rebasedNoSequence = rebased.CloudForms.Single(record => record.EditorId == noSequence.EditorId);
        Assert.Equal(0x0000_1001u, rebasedNoSequence.FormId);
        Assert.Equal(0u, Assert.IsType<StarfieldCloudFormDefinition>(rebasedNoSequence.Definition)
            .CloudCardSequenceFormId);
        Assert.DoesNotContain(0u, mappedFormIds);

        var rebasedMalformed = rebased.CloudForms.Single(record => record.EditorId == malformed.EditorId);
        Assert.Equal(0x0000_1002u, rebasedMalformed.FormId);
        Assert.Null(rebasedMalformed.Definition);
        Assert.Equal("CLDF REFL schema mismatch", rebasedMalformed.DecodeFailure);
        Assert.Equal(0x3456L, rebasedMalformed.Offset);
        Assert.True(rebasedMalformed.IsBigEndian);

        Assert.Equal(0x0100_2000u, full.Definition!.CloudCardSequenceFormId);
        Assert.Same(sourceLayers, full.Definition.Layers);
        Assert.Same(sourcePlanes, full.Definition.Planes);
        Assert.Equal(0u, noSequence.Definition!.CloudCardSequenceFormId);
        Assert.Null(malformed.Definition);
    }

    [Fact]
    public void Tes4_namespacing_rebases_starfield_cloud_card_before_formid_merge()
    {
        var masters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Base.esm"] = [],
            ["Spacer.esm"] = ["Base.esm"],
            ["Clouds.esm"] = ["Base.esm"]
        };
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Base.esm", "Spacer.esm", "Clouds.esm"],
            mastersReader: path => masters[Path.GetFileName(path)]);
        Assert.NotNull(mapper);

        // Both source plugins use local slot 1 for their own records. Without source-before-merge
        // namespacing these CLDFs collide and the later Clouds.esm record silently replaces the first.
        var spacerSource = new RecordCollection
        {
            CloudForms =
            [
                new StarfieldCloudFormRecord
                {
                    FormId = 0x0100_1000u,
                    EditorId = "SpacerCloud",
                    Definition = CreateCloudDefinition(0)
                }
            ]
        };
        var cloudsSource = new RecordCollection
        {
            CloudForms =
            [
                new StarfieldCloudFormRecord
                {
                    FormId = 0x0100_1000u,
                    EditorId = "CloudsPluginCloud",
                    Definition = CreateCloudDefinition(0x0100_2000u)
                }
            ]
        };

        var namespacedSpacer = mapper!.Namespaced(spacerSource, "Spacer.esm");
        var namespacedClouds = mapper.Namespaced(cloudsSource, "Clouds.esm");
        var merged = namespacedSpacer.MergeWith(namespacedClouds);

        Assert.Equal(2, merged.CloudForms.Count);
        var spacerCloud = merged.CloudForms.Single(record => record.EditorId == "SpacerCloud");
        Assert.Equal(0x0100_1000u, spacerCloud.FormId);
        Assert.Equal(0u, spacerCloud.Definition!.CloudCardSequenceFormId);
        var pluginCloud = merged.CloudForms.Single(record => record.EditorId == "CloudsPluginCloud");
        Assert.Equal(0x0200_1000u, pluginCloud.FormId);
        Assert.Equal(0x0200_2000u, pluginCloud.Definition!.CloudCardSequenceFormId);

        Assert.Equal(0x0100_1000u, cloudsSource.CloudForms[0].FormId);
        Assert.Equal(0x0100_2000u, cloudsSource.CloudForms[0].Definition!.CloudCardSequenceFormId);
    }

    [Fact]
    public void RecordCollectionFormIdRebaser_rebases_starfield_atmo_without_aliasing_or_mapping_zero()
    {
        var rootPatch = new StarfieldAtmospherePatch
        {
            ParentFormId = 0,
            SunPresetOverrideFormId = 0,
            ClimateOverrideFormId = 0
        };
        var diffPatch = new StarfieldAtmospherePatch
        {
            ParentFormId = 0x0100_1000u,
            SunPresetOverrideFormId = 0,
            ClimateOverrideFormId = 0x0100_2000u
        };
        var root = new StarfieldAtmosphereRecord
        {
            FormId = 0x0100_1000u,
            EditorId = "AtmosphereRoot",
            PayloadKind = StarfieldAtmospherePayloadKind.FullObject,
            Patch = rootPatch,
            Offset = 0x1234
        };
        var diff = new StarfieldAtmosphereRecord
        {
            FormId = 0x0100_1001u,
            EditorId = "AtmosphereDiff",
            ParentFormId = 0x0100_1000u,
            PayloadKind = StarfieldAtmospherePayloadKind.Diff,
            Patch = diffPatch,
            DecodeFailure = "retained diagnostic",
            Offset = 0x2345,
            IsBigEndian = true
        };
        var source = new RecordCollection { Atmospheres = [root, diff] };
        var mapped = new List<uint>();

        var rebased = RecordCollectionFormIdRebaser.Rebase(source, formId =>
        {
            mapped.Add(formId);
            return formId & 0x00FF_FFFFu;
        });

        var rebasedRoot = rebased.Atmospheres.Single(record => record.EditorId == root.EditorId);
        Assert.Equal(0x0000_1000u, rebasedRoot.FormId);
        Assert.Null(rebasedRoot.ParentFormId);
        Assert.NotSame(root, rebasedRoot);
        Assert.NotSame(rootPatch, rebasedRoot.Patch);
        Assert.Equal(0u, rebasedRoot.Patch!.ParentFormId);
        Assert.Equal(0u, rebasedRoot.Patch.SunPresetOverrideFormId);
        Assert.Equal(0u, rebasedRoot.Patch.ClimateOverrideFormId);
        Assert.Equal(0x1234L, rebasedRoot.Offset);

        var rebasedDiff = rebased.Atmospheres.Single(record => record.EditorId == diff.EditorId);
        Assert.Equal(0x0000_1001u, rebasedDiff.FormId);
        Assert.Equal(0x0000_1000u, rebasedDiff.ParentFormId);
        Assert.NotSame(diff, rebasedDiff);
        Assert.NotSame(diffPatch, rebasedDiff.Patch);
        Assert.Equal(0x0000_1000u, rebasedDiff.Patch!.ParentFormId);
        Assert.Equal(0u, rebasedDiff.Patch.SunPresetOverrideFormId);
        Assert.Equal(0x0000_2000u, rebasedDiff.Patch.ClimateOverrideFormId);
        Assert.Equal("retained diagnostic", rebasedDiff.DecodeFailure);
        Assert.Equal(0x2345L, rebasedDiff.Offset);
        Assert.True(rebasedDiff.IsBigEndian);
        Assert.DoesNotContain(0u, mapped);

        Assert.Equal(0x0100_1000u, root.FormId);
        Assert.Same(rootPatch, root.Patch);
        Assert.Equal(0x0100_1001u, diff.FormId);
        Assert.Equal(0x0100_1000u, diff.ParentFormId);
        Assert.Same(diffPatch, diff.Patch);
        Assert.Equal(0x0100_1000u, diffPatch.ParentFormId);
        Assert.Equal(0x0100_2000u, diffPatch.ClimateOverrideFormId);
    }

    [Theory]
    [InlineData("WeatherSettingsFormId")]
    [InlineData("ParentFormId")]
    [InlineData("DisplayNameKeywordFormId")]
    [InlineData("ImageSpaceFormId")]
    [InlineData("ImageSpaceNightFormId")]
    [InlineData("VolumetricLightingFormId")]
    [InlineData("CloudsFormId")]
    [InlineData("PrecipitationEffectFormId")]
    [InlineData("OptionalPhotoModeEffectFormId")]
    [InlineData("LensFlareFormId")]
    [InlineData("WindForceFormId")]
    [InlineData("SubWeatherFormIds")]
    [InlineData("SunPresetOverrideFormId")]
    [InlineData("ClimateOverrideFormId")]
    public void EsmFormIdPropertyRegistry_includes_starfield_weather_settings_references(string propertyName)
    {
        Assert.True(EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName));
    }

    [Fact]
    public void EsmFormIdPropertyRegistry_includes_starfield_cloud_card_sequence_reference()
    {
        Assert.True(EsmFormIdPropertyRegistry.IsFormIdProperty("CloudCardSequenceFormId"));
    }

    [Theory]
    [InlineData("RiverAbsorptionCurveFormId")]
    [InlineData("OceanAbsorptionCurveFormId")]
    [InlineData("RiverScatteringCurveFormId")]
    [InlineData("OceanScatteringCurveFormId")]
    [InlineData("PhytoplanktonCurveFormId")]
    [InlineData("SedimentCurveFormId")]
    [InlineData("YellowMatterCurveFormId")]
    public void EsmFormIdPropertyRegistry_includes_all_starfield_water_curve_references(
        string propertyName)
    {
        Assert.True(EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName));
    }

    [Fact]
    public void RecordCollectionFormIdRebaser_rebases_all_starfield_water_curve_references()
    {
        var authored = new StarfieldWaterVisualData
        {
            Flags = StarfieldWaterFlags.EnableFlowmap | StarfieldWaterFlags.BlendNormals,
            Gnam = new StarfieldWaterUnusedGnam
            {
                Word0 = 0x7FC0_0000u,
                Word1 = 0xFFFF_FFFFu,
                Word2 = 0x0123_4567u
            },
            Dnam = new StarfieldWaterDnam
            {
                DepthAmount = 8f,
                Layer1 = new StarfieldWaterNoiseLayer
                {
                    WindDirection = 40f,
                    WindSpeed = 0.019f,
                    AmplitudeScale = 0.9f,
                    UvScale = 72f,
                    NoiseFalloff = 100f
                }
            },
            RiverAbsorptionCurveFormId = 0x0100_0001u,
            OceanAbsorptionCurveFormId = 0x0100_0002u,
            RiverScatteringCurveFormId = 0x0100_0003u,
            OceanScatteringCurveFormId = 0x0100_0004u,
            PhytoplanktonCurveFormId = 0x0100_0005u,
            SedimentCurveFormId = 0x0100_0006u,
            YellowMatterCurveFormId = 0x0100_0007u
        };
        var records = new RecordCollection
        {
            Water =
            [
                new WaterRecord
                {
                    FormId = 0x0100_1234u,
                    VisualProperties = new Dictionary<string, object?>
                    {
                        ["StarfieldVisualData"] = authored
                    }
                }
            ]
        };

        var rebased = RecordCollectionFormIdRebaser.Rebase(
            records,
            formId => formId & 0x00FF_FFFFu);

        var rebasedWater = Assert.Single(rebased.Water);
        var rebasedVisual = Assert.IsType<StarfieldWaterVisualData>(
            rebasedWater.VisualProperties!["StarfieldVisualData"]);
        Assert.Equal(8f, rebasedVisual.Dnam.DepthAmount);
        Assert.Equal(72f, rebasedVisual.Dnam.Layer1.UvScale);
        Assert.Equal(StarfieldWaterFlags.EnableFlowmap | StarfieldWaterFlags.BlendNormals,
            rebasedVisual.Flags);
        var rebasedGnam = Assert.IsType<StarfieldWaterUnusedGnam>(rebasedVisual.Gnam);
        Assert.Equal(0x7FC0_0000u, rebasedGnam.Word0);
        Assert.Equal(0xFFFF_FFFFu, rebasedGnam.Word1);
        Assert.Equal(0x0123_4567u, rebasedGnam.Word2);
        Assert.Equal(0x0000_0001u, rebasedVisual.RiverAbsorptionCurveFormId!.Value);
        Assert.Equal(0x0000_0002u, rebasedVisual.OceanAbsorptionCurveFormId!.Value);
        Assert.Equal(0x0000_0003u, rebasedVisual.RiverScatteringCurveFormId!.Value);
        Assert.Equal(0x0000_0004u, rebasedVisual.OceanScatteringCurveFormId!.Value);
        Assert.Equal(0x0000_0005u, rebasedVisual.PhytoplanktonCurveFormId!.Value);
        Assert.Equal(0x0000_0006u, rebasedVisual.SedimentCurveFormId!.Value);
        Assert.Equal(0x0000_0007u, rebasedVisual.YellowMatterCurveFormId!.Value);

        Assert.Equal(0x0100_0001u, authored.RiverAbsorptionCurveFormId!.Value);
        Assert.Equal(0x0100_0007u, authored.YellowMatterCurveFormId!.Value);
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

    private static StarfieldCloudFormDefinition CreateCloudDefinition(
        uint cloudCardSequenceFormId,
        IReadOnlyList<StarfieldCloudLayer>? layers = null,
        IReadOnlyList<StarfieldCloudPlane>? planes = null)
    {
        return new StarfieldCloudFormDefinition(
            new StarfieldCloudShadowParams(false, string.Empty, 0f, 0f, 0f, 0f),
            layers ?? [],
            planes ?? [],
            cloudCardSequenceFormId);
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

    private string WriteTes3Esm(string fileName, params string[] masters)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, BuildHeaderOnlyTes3Esm(masters));
        return path;
    }

    /// <summary>
    ///     Synthetic TES3 (Morrowind) header-only plugin: 16-byte record header (sig + u32 dataSize +
    ///     u32 + u32 flags), subrecords with u32 sizes (8-byte headers, unlike TES4's u16). HEDR is the
    ///     full 300 bytes (version, fileType, author[32], description[256], record count); masters are
    ///     MAST zstring + DATA u64 pairs.
    /// </summary>
    private static byte[] BuildHeaderOnlyTes3Esm(params string[] masters)
    {
        var hedr = new byte[300];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.3f);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(4), 1);
        Encoding.ASCII.GetBytes("test", hedr.AsSpan(8, 4)); // author, NUL-padded

        var subrecords = new List<(string Signature, byte[] Data)> { ("HEDR", hedr) };
        foreach (var master in masters)
        {
            subrecords.Add(("MAST", Encoding.ASCII.GetBytes(master + "\0")));
            subrecords.Add(("DATA", new byte[8]));
        }

        var dataSize = subrecords.Sum(subrecord => 8 + subrecord.Data.Length);
        var data = new byte[16 + dataSize];
        Encoding.ASCII.GetBytes("TES3", data.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)dataSize);

        var offset = 16;
        foreach (var (signature, bytes) in subrecords)
        {
            Encoding.ASCII.GetBytes(signature, data.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), (uint)bytes.Length);
            bytes.CopyTo(data.AsSpan(offset + 8));
            offset += 8 + bytes.Length;
        }

        return data;
    }
}
