using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic.LoadOrder;

public sealed class LoadOrderDerivedViewTests
{
    [Theory]
    [InlineData("retained", 0x800u, 1)]
    [InlineData("moved", 0x801u, 1)]
    [InlineData("deleted", 0x800u, 0)]
    [InlineData("unparsed", 0x800u, 0)]
    [InlineData("duplicate", 0x800u, 0)]
    public void Dialogue_children_obey_their_own_winner_even_when_the_parent_changes(string scenario, uint topicId, int count)
    {
        var order = Order();
        var versions = new List<LoadOrderRecordVersion>
        {
            V("A.esm", 0x800, "DIAL"), V("A.esm", 0x900, "INFO"),
            V("B.esm", 0x800, "DIAL"), V("B.esm", 0x801, "DIAL")
        };
        if (scenario != "retained") { versions.Add(V("B.esm", 0x900, "INFO", scenario == "deleted" ? 0x20u : 0)); }
        if (scenario == "duplicate") { versions.Add(V("B.esm", 0x900, "INFO") with { Offset = 100 }); }
        var old = new RecordCollection
        {
            DialogTopics = [new() { FormId = 0x800, FullName = "Old parent" }],
            Dialogues = [new() { FormId = 0x900, TopicFormId = 0x800, PromptText = "retained child" }]
        };
        var later = new RecordCollection
        {
            DialogTopics = [new() { FormId = 0x800, FullName = "New parent" }, new() { FormId = 0x801 }],
            Dialogues = scenario is "moved" or "duplicate" ? [new() { FormId = 0x900, TopicFormId = topicId }] : []
        };
        var view = LoadOrderSelectionView.FromSources(order, LoadOrderRecordIndex.Create(order, versions),
            [(order.Entries[0], old), (order.Entries[1], later)]);

        Assert.Equal(count, view.Records.Dialogues.Count);
        Assert.Equal("New parent", view.Records.DialogTopics.Single(t => t.FormId == 0x800).FullName);
        var displayed = view.Records.DialogueTree!.OrphanTopics.SelectMany(t => t.InfoChain).ToArray();
        Assert.Equal(count, displayed.Length);
        if (count > 0)
        {
            Assert.Equal(topicId, displayed[0].Info.TopicFormId);
            Assert.Contains(view.Records.DialogueTree.Edges, e => e.SourceInfoFormId == 0x900 && e.TargetFormId == topicId);
        }
        Assert.Equal("Old parent", old.DialogTopics[0].FullName);
    }

    [Theory]
    [InlineData("retained", true, 1)]
    [InlineData("moved", true, 1)]
    [InlineData("deleted", false, 0)]
    [InlineData("unparsed", false, 0)]
    [InlineData("duplicate", false, 0)]
    [InlineData("two-live-lands", false, 2)]
    [InlineData("parent-deleted", false, 1)]
    public void Terrain_is_selected_independently_and_ambiguous_or_unattached_components_stay_visible(
        string scenario, bool heightVisible, int componentCount)
    {
        var order = Order();
        var versions = new List<LoadOrderRecordVersion> { V("A.esm", 0x800, "CELL"), V("A.esm", 0x900, "LAND"),
            V("B.esm", 0x800, "CELL", scenario == "parent-deleted" ? 0x20u : 0), V("B.esm", 0x801, "CELL") };
        if (scenario is "moved" or "deleted" or "unparsed" or "duplicate")
        {
            versions.Add(V("B.esm", 0x900, "LAND", scenario == "deleted" ? 0x20u : 0));
        }
        if (scenario == "duplicate") { versions.Add(V("B.esm", 0x900, "LAND") with { Offset = 100 }); }
        if (scenario == "two-live-lands") { versions.Add(V("B.esm", 0x901, "LAND")); }
        var oldHeight = new LandHeightmap { HeightDeltas = new sbyte[1089], HeightOffset = 10 };
        var terrain = new List<SelectedLandComponent> { new(0x900, 0x800, "A.esm", "A.esm", 0x900, 24, oldHeight, null) };
        if (scenario == "moved") { terrain.Add(new(0x900, 0x801, "B.esm", "B.esm", 0x900, 24, oldHeight with { HeightOffset = 20 }, null)); }
        if (scenario == "two-live-lands") { terrain.Add(new(0x901, 0x800, "B.esm", "B.esm", 0x901, 24, oldHeight, null)); }
        var view = LoadOrderSelectionView.FromSources(order, LoadOrderRecordIndex.Create(order, versions),
        [
            (order.Entries[0], new RecordCollection { Cells = [new() { FormId = 0x800, WaterHeight = 123 }] }),
            (order.Entries[1], new RecordCollection { Cells = [new() { FormId = 0x800 }, new() { FormId = 0x801 }] })
        ], terrain);

        Assert.Equal(componentCount, view.TerrainComponents.Count);
        Assert.Equal(heightVisible, view.Records.Cells.Any(c => c.Heightmap != null));
        Assert.All(view.Records.Cells, c => Assert.Null(c.WaterHeight));
        if (scenario == "moved") { Assert.Equal(20, view.Records.Cells.Single(c => c.FormId == 0x801).Heightmap!.HeightOffset); }
        var csv = SelectedViewReportWriter.Generate(view)["world_components.csv"];
        if (scenario == "parent-deleted") { Assert.Contains("parent-unresolved", csv, StringComparison.Ordinal); }
        if (scenario == "two-live-lands") { Assert.Contains("ambiguous-cell-land", csv, StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData("chain", null)]
    [InlineData("dangling", "previous-info-outside-topic-or-unresolved")]
    [InlineData("fork", "predecessor-fork")]
    [InlineData("cycle", "predecessor-cycle-or-dependent-on-cycle")]
    [InlineData("tie", "unconstrained-display-order")]
    public void Dialogue_order_constraints_report_uncertainty_without_dropping_replies(string scenario, string? issue)
    {
        var records = new RecordCollection
        {
            DialogTopics = [new() { FormId = 0x800, EditorId = "Topic" }],
            Dialogues =
            [
                new() { FormId = 0x903, TopicFormId = 0x800, PreviousInfo = scenario == "fork" ? 0x901u : scenario == "tie" ? null : 0x902u },
                new() { FormId = 0x901, TopicFormId = 0x800, PreviousInfo = scenario == "cycle" ? 0x903u : null,
                    RawParentTopicFormIds = [0x800], LinkToTopics = [0xBAD], FollowUpInfos = [0x902] },
                new() { FormId = 0x902, TopicFormId = 0x800, PreviousInfo = scenario == "dangling" ? 0xBAEu : scenario == "tie" ? null : 0x901u }
            ]
        };
        var tree = SelectedDialogueViewBuilder.Build(records);
        Assert.Equal(3, Assert.Single(tree.OrphanTopics).InfoChain.Count);
        if (issue == null) { Assert.Empty(tree.OrderingIssues); Assert.Equal([0x901u, 0x902u, 0x903u], tree.OrphanTopics[0].InfoChain.Select(n => n.Info.FormId)); }
        else { Assert.Contains(tree.OrderingIssues, i => i.Code == issue); }
        Assert.Contains(tree.Edges, e => e.TargetFormId == 0xBAD && e.Status == "unresolved" && e.Evidence == "explicit-TCLT");
        Assert.Contains(tree.Edges, e => e.Evidence == "physical-GRUP" && e.TargetFormId == 0x800);
        Assert.Contains(tree.Edges, e => e.Evidence == "observed-runtime" && e.TargetFormId == 0x902);
    }

    [Fact]
    public void Parsed_snapshot_uses_physical_land_parent_and_rebases_texture_and_dialogue_links()
    {
        var order = PluginLoadOrder.Create(["A.esm", "Other.esm", "Dlc.esm"], false,
            p => p == "A.esm" ? [] : ["A.esm"]);
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Cells = [new CellRecord { FormId = 0x01000800, Offset = 24 }],
            DialogTopics = [new() { FormId = 0x01000A00 }],
            Dialogues = [new() { FormId = 0x01000A01, TopicFormId = 0x01000A00, RawParentTopicFormIds = [0x01000A00] }]
        };
        var header = new DetectedMainRecord("LAND", 20, 0, 0x01000900, 72, false);
        var scan = new EsmRecordScanResult
        {
            Game = records.Game,
            MainRecords = [new("CELL", 20, 0, 0x01000800, 24, false), header,
                new("DIAL", 20, 0, 0x01000A00, 110, false), new("INFO", 20, 0, 0x01000A01, 140, false)],
            PlacementGroups = [new GrupHeaderInfo { Offset = 48, GroupSize = 52, GroupType = 9, Label = BitConverter.GetBytes(0x01000800u) }],
            LandRecords = [new() { Header = header, ParentCellFormId = 0xDEAD, // stale inferred label must lose to framing
                ParsedHeightmap = new() { HeightDeltas = new sbyte[1089] },
                VisualData = new() { TextureLayers = [new() { Kind = LandTextureLayerKind.Base, TextureFormId = 0x01000B00 }] } }]
        };
        var view = LoadOrderSelectionView.FromParsedSources(order, [(order.Entries[2], records, scan)]);
        var cell = Assert.Single(view.Records.Cells);
        Assert.Equal(0x02000800u, cell.FormId);
        Assert.NotNull(cell.Heightmap);
        Assert.Equal(0x02000800u, cell.Heightmap.SourceParentCellFormId);
        Assert.Equal(0x02000B00u, cell.LandVisualData!.TextureLayers[0].TextureFormId);
        Assert.Equal(0x02000800u, Assert.Single(view.TerrainComponents).ParentCellFormId);
        Assert.Contains(view.Records.DialogueTree!.Edges, e => e.TargetFormId == 0x02000A00 && e.Evidence == "physical-GRUP");
        Assert.Equal(0x01000A00u, records.Dialogues[0].RawParentTopicFormId);
    }

    [Fact]
    public void Placement_with_a_missing_physical_parent_is_retained_without_inventing_a_cell()
    {
        var order = PluginLoadOrder.Create(["A.esm"], false, _ => []);
        var records = new RecordCollection
        {
            Cells = [new() { FormId = 0x800, Offset = 24,
                PlacedObjects = [new() { FormId = 0x900, BaseFormId = 0xA00, Offset = 72, X = 1.2345678f }] }]
        };
        var scan = new EsmRecordScanResult
        {
            MainRecords = [new("CELL", 20, 0, 0x800, 24, false), new("REFR", 20, 0, 0x900, 72, false)],
            PlacementGroups = [new() { Offset = 48, GroupSize = 52, GroupType = 9, Label = BitConverter.GetBytes(0xBADu) }]
        };
        var view = LoadOrderSelectionView.FromParsedSources(order, [(order.Entries[0], records, scan)]);
        Assert.Empty(Assert.Single(view.Records.Cells).PlacedObjects);
        var component = Assert.Single(view.PlacementComponents);
        Assert.Equal(0xBADu, component.CellFormId);
        Assert.Equal(0x900u, component.Reference.FormId);
        Assert.DoesNotContain(view.Records.Cells, c => c.FormId == 0xBAD);
        Assert.Contains("parent-unresolved", SelectedViewReportWriter.Generate(view)["selected_placements.csv"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parent_attribution_is_rebuilt_but_record_local_attribution_survives(bool ownAttribution)
    {
        var order = Order();
        var old = new RecordCollection
        {
            DialogTopics = [new() { FormId = 0x800, FullName = "Old child prompt" }],
            Dialogues = [new()
            {
                FormId = 0x900, TopicFormId = 0x800, RawParentTopicFormIds = [0x800],
                QuestFormId = 0xA00, SpeakerFormId = 0xB00, SpeakerVoiceTypeFormId = 0xC00,
                LocalAttribution = new() { QuestFormId = ownAttribution ? 0xA00u : null, SpeakerFormId = ownAttribution ? 0xB00u : null }
            }]
        };
        var later = new RecordCollection
        {
            DialogTopics = [new() { FormId = 0x800, FullName = "Stale promoted text", QuestFormId = 0xA01, SpeakerFormId = 0xB01,
                LocalAttribution = new() { QuestFormId = 0xA01, SpeakerFormId = 0xB01 } }],
            FormIdToDisplayName = new() { [0x800] = "Stale promoted text" }
        };
        var view = LoadOrderSelectionView.FromSources(order, LoadOrderRecordIndex.Create(order,
            [V("A.esm", 0x800, "DIAL"), V("A.esm", 0x900, "INFO"), V("B.esm", 0x800, "DIAL")]),
            [(order.Entries[0], old), (order.Entries[1], later)]);
        var info = Assert.Single(view.Records.Dialogues);
        Assert.Equal(ownAttribution ? 0xA00u : 0xA01u, info.QuestFormId);
        Assert.Equal(ownAttribution ? 0xB00u : 0xB01u, info.SpeakerFormId);
        Assert.Null(info.SpeakerVoiceTypeFormId); // sibling-majority cache is not an authored binding
        Assert.Null(Assert.Single(view.Records.DialogTopics).FullName);
        Assert.False(view.Records.FormIdToDisplayName.ContainsKey(0x800));
        Assert.Contains(view.Records.DialogueTree!.Edges, e => e.Kind == "quest-membership" &&
            e.Evidence == (ownAttribution ? "record-local-QSTI" : "inferred-parent-DIAL"));
        Assert.Equal("Stale promoted text", later.DialogTopics[0].FullName);
        Assert.Equal(0xC00u, old.Dialogues[0].SpeakerVoiceTypeFormId);
    }

    [Theory]
    [InlineData("A.esm", "A.esm;Dlc.esm;Patch.esp")]
    [InlineData("Dlc.esm", "A.esm;Dlc.esm;Patch.esp")]
    [InlineData("Patch.esp", "A.esm;Dlc.esm;Patch.esp")]
    public void Primary_plugin_insertion_preserves_explicit_supplementary_order(string primary, string expected)
    {
        var all = new[] { "A.esm", "Dlc.esm", "Patch.esp" };
        var order = PrimaryPluginOrder.Create(primary, all.Where(p => p != primary).ToArray(),
            p => Path.GetFileName(p) switch { "Dlc.esm" => ["A.esm"], "Patch.esp" => ["A.esm", "Dlc.esm"], _ => [] });
        Assert.Equal(expected, string.Join(";", order.Entries.Select(e => e.Name)));
        Assert.Throws<ArgumentException>(() => PrimaryPluginOrder.Create("Unrelated.esm", ["Patch.esp", "A.esm"],
            p => Path.GetFileName(p) == "Patch.esp" ? ["A.esm"] : []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parsed_ammo_snapshot_keeps_direct_links_and_discards_source_weapon_inference(bool authored)
    {
        var order = Order();
        var records = new RecordCollection
        {
            Ammo = [new() { FormId = 0x800, ProjectileFormId = 0xBAD, HasRecordLocalProjectileSnapshot = true,
                RecordLocalProjectileFormId = authored ? 0x900u : null }]
        };
        var view = LoadOrderSelectionView.FromSources(order,
            LoadOrderRecordIndex.Create(order, [V("A.esm", 0x800, "AMMO")]), [(order.Entries[0], records)]);
        Assert.Equal(authored ? 0x900u : (uint?)null, Assert.Single(view.Records.Ammo).ProjectileFormId);
        Assert.Equal(0xBADu, records.Ammo[0].ProjectileFormId);
    }

    [Theory]
    [InlineData("winner", 0x01000900u, "Dlc.esm", true)]
    [InlineData("captured", 0x05000900u, null, false)]
    [InlineData("ambiguous", 0x02000900u, null, false)]
    public void Selected_dialogue_audio_uses_physical_local_identity_or_requires_an_explicit_origin(
        string scenario, uint expectedId, string? expectedPath, bool expectedHeader)
    {
        var order = PluginLoadOrder.Create(["A.esm", "Other.esm", "Dlc.esm"], false,
            p => p == "A.esm" ? [] : ["A.esm"]);
        var version = new LoadOrderRecordVersion("Dlc.esm", "Dlc.esm", 0x01000900, 0x02000900,
            "INFO", null, 0, 24);
        var index = LoadOrderRecordIndex.Create(order, scenario == "ambiguous"
            ? [version, version with { Offset = 120 }] : [version]);
        var info = new DialogueRecord { FormId = 0x02000900,
            AudioSourceInfoFormId = scenario == "captured" ? 0x05000900u : null };

        var origin = LoadOrderAudioOrigin.Resolve(info, index, "A.esm", true);

        Assert.Equal(expectedId, origin.InfoFormId);
        Assert.Equal(expectedPath, origin.RecordPath);
        Assert.Equal(expectedHeader, origin.UsePluginHeader);
    }

    [Fact]
    public void Parsed_snapshot_keeps_each_physical_editor_id_even_when_the_typed_map_collapses_duplicates()
    {
        var order = PluginLoadOrder.Create(["A.esm"], false, _ => []);
        var records = new RecordCollection { FormIdToEditorId = new() { [0x800] = "Collapsed" } };
        var scan = new EsmRecordScanResult
        {
            MainRecords = [new("DIAL", 20, 0, 0x800, 24, false), new("DIAL", 20, 0, 0x800, 120, false)],
            EditorIds = [new("First", 24), new("Second", 120)]
        };
        var view = LoadOrderSelectionView.FromParsedSources(order, [(order.Entries[0], records, scan)]);

        Assert.Equal(["First", "Second"], view.Index.Records[0x800].Versions.Select(v => v.EditorId));
        Assert.Equal("excluded-ambiguous-winner", view.Status(view.Index.Records[0x800]));
        Assert.Same(view.Resolver, view.Resolver);
        Assert.False(view.Resolver.EditorIds.ContainsKey(0x800));
    }

    [Theory]
    [InlineData("overridden", "included-in-typed-view")]
    [InlineData("template-deleted", "excluded-deleted")]
    [InlineData("template-type-conflict", "excluded-type-conflict")]
    [InlineData("parent-deleted", "excluded-deleted")]
    [InlineData("parent-unparsed", "not-in-typed-view")]
    [InlineData("parent-type-conflict", "excluded-type-conflict")]
    public void Component_report_uses_selected_parent_and_template_with_physical_provenance(string scenario, string expectedStatus)
    {
        var order = Order();
        var original = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Cells = [new() { FormId = 0x800, LightingTemplateFormId = 0x900,
                LightingTemplateInheritanceFlags = 1 << 8,
                LightingData = new Dictionary<string, object?> { ["FogPow"] = 1.25f } }],
            LightingTemplates = [new() { FormId = 0x900, LightingData = new() { ["FogPower"] = 9f } }],
            Worldspaces = [new() { FormId = 0xA00, ParentWorldspaceFormId = 0xA01,
                ParentUseFlags = 1 << 3, DefaultWaterHeight = 100f },
                new() { FormId = 0xA01, DefaultWaterHeight = 90f }]
        };
        var later = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            LightingTemplates = [new() { FormId = 0x900, LightingData = new() { ["FogPower"] = 2.5f } }],
            Worldspaces = scenario == "parent-unparsed" ? [] : [new() { FormId = 0xA01,
                DefaultWaterHeight = 12.75f, WaterFormId = 0x800, ClimateFormId = 0x800, ImageSpaceFormId = 0x800 }]
        };
        var index = LoadOrderRecordIndex.Create(order,
        [
            V("A.esm", 0x800, "CELL"), V("A.esm", 0x900, "LGTM"), V("A.esm", 0xA00, "WRLD"),
            V("A.esm", 0xA01, "WRLD"),
            V("B.esm", 0xA01, scenario == "parent-type-conflict" ? "BOOK" : "WRLD", scenario == "parent-deleted" ? 0x20u : 0),
            V("B.esm", 0x900, scenario == "template-type-conflict" ? "BOOK" : "LGTM", scenario == "template-deleted" ? 0x20u : 0)
        ]);
        var view = LoadOrderSelectionView.FromSources(order, index,
            [(order.Entries[0], original), (order.Entries[1], later)]);
        var csv = SelectedViewReportWriter.Generate(view)["world_inheritance.csv"];
        var excludedParent = scenario.StartsWith("parent-", StringComparison.Ordinal);
        Assert.Contains(excludedParent
            ? $"0x00000A00,world-water-height,,,,Unavailable,missing-parent,0x00000A00;0x00000A01,,,,unset,0x00000A01,{expectedStatus}"
            : "0x00000A00,world-water-height,0x00000A01,,12.75,Reconstruction,inherited,0x00000A00;0x00000A01,B.esm,B.esm,24,unset,0x00000A01,included-in-typed-view", csv);
        Assert.Contains(scenario.StartsWith("template-", StringComparison.Ordinal)
            ? $"0x00000800,lighting-FogPower,0x00000800,,1.25,Reconstruction,cell;inherit-requested;fallback,0x00000800,A.esm,A.esm,24,unset,0x00000900,{expectedStatus}"
            : "0x00000800,lighting-FogPower,0x00000900,,2.5,Reconstruction,template;inherit-requested,0x00000800;0x00000900,B.esm,B.esm,24,unset,0x00000900,included-in-typed-view", csv);
        if (!excludedParent)
        {
            foreach (var component in new[] { "world-water-type", "world-climate", "world-image-space" })
                Assert.Contains($"0x00000A01,{component},0x00000A01,0x00000800,,Reconstruction,direct,0x00000A01,B.esm,B.esm,24,wrong-type", csv);
        }
        Assert.DoesNotContain(",9,Reconstruction", csv);
        Assert.Equal(100f, view.Records.Worldspaces.Single(w => w.FormId == 0xA00).DefaultWaterHeight);
        Assert.Equal(9f, original.LightingTemplates[0].LightingData!["FogPower"]);
        Assert.Equal(1.25f, original.Cells[0].LightingData!["FogPow"]);
        Assert.Equal(90f, original.Worldspaces[1].DefaultWaterHeight);
        Assert.Equal(2.5f, later.LightingTemplates[0].LightingData!["FogPower"]);
    }

    [Theory]
    [InlineData("present", "resolved")]
    [InlineData("missing", "unresolved")]
    [InlineData("deleted", "unresolved")]
    [InlineData("unparsed", "unresolved")]
    [InlineData("type-conflict", "unresolved")]
    [InlineData("duplicate", "unresolved")]
    public void Reverse_topic_links_retain_selected_evidence_owners_and_unresolved_destinations(string scenario, string status)
    {
        var order = Order();
        var versions = new List<LoadOrderRecordVersion>
        {
            V("A.esm", 0x800, "DIAL"), V("A.esm", 0x900, "INFO") with { Offset = 41 },
            V("A.esm", 0x901, "INFO") with { Offset = 67 }, V("A.esm", 0x902, "INFO") with { Offset = 83 },
            V("B.esm", 0x901, "INFO") with { Offset = 101 }
        };
        if (scenario != "missing") { versions.Add(V("A.esm", 0x801, "DIAL")); }
        if (scenario is "deleted" or "unparsed" or "type-conflict" or "duplicate")
        {
            versions.Add(V("B.esm", 0x801, scenario == "type-conflict" ? "BOOK" : "DIAL",
                scenario == "deleted" ? 0x20u : 0));
        }
        if (scenario == "duplicate") { versions.Add(V("B.esm", 0x801, "DIAL") with { Offset = 200 }); }
        var original = new RecordCollection
        {
            DialogTopics = scenario == "missing" ? [new() { FormId = 0x800 }] :
                [new() { FormId = 0x800 }, new() { FormId = 0x801 }],
            Dialogues =
            [
                new() { FormId = 0x900, TopicFormId = 0x800 },
                new() { FormId = 0x901, TopicFormId = 0x801, LinkFromTopics = [0x800] },
                new() { FormId = 0x902, TopicFormId = 0x801, LinkFromTopics = [0x800] }
            ]
        };
        var replacement = new RecordCollection
        {
            Dialogues = [new() { FormId = 0x901, TopicFormId = 0x801, LinkFromTopics = [0x800] }],
            DialogTopics = scenario is "deleted" or "duplicate" ? [new() { FormId = 0x801 }] : []
        };
        var view = LoadOrderSelectionView.FromSources(order, LoadOrderRecordIndex.Create(order, versions),
            [(order.Entries[0], original), (order.Entries[1], replacement)]);

        var tree = view.Records.DialogueTree!;
        var inferred = Assert.Single(tree.Edges.Where(edge => edge.Evidence == "inferred-reverse-TCLF"));
        Assert.Equal(0x900u, inferred.SourceInfoFormId);
        Assert.Equal(0x801u, inferred.TargetFormId);
        Assert.Equal(status, inferred.Status);
        Assert.Equal(new[] { 0x901u, 0x902u }, inferred.EvidenceInfoFormIds.Order());
        Assert.Equal(3, tree.OrphanTopics.Sum(topic => topic.InfoChain.Count));
        Assert.Contains(tree.Edges, edge => edge.SourceInfoFormId == 0x901 &&
            edge.Kind == "topic-membership" && edge.Status == status);
        var csv = SelectedViewReportWriter.Generate(view)["dialogue_links.csv"];
        Assert.Contains($"0x00000900,0x00000801,choice-topic,inferred-reverse-TCLF,{status},A.esm,A.esm,41,0x00000901,B.esm,B.esm,101", csv);
        Assert.Contains($"0x00000900,0x00000801,choice-topic,inferred-reverse-TCLF,{status},A.esm,A.esm,41,0x00000902,A.esm,A.esm,83", csv);
        Assert.DoesNotContain(",0x00000901,A.esm,A.esm,67", csv);
        Assert.All(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries), row => Assert.Equal(12, row.Split(',').Length));
        Assert.Equal(new[] { 0x800u }, original.Dialogues[1].LinkFromTopics);
    }

    private static PluginLoadOrder Order() => PluginLoadOrder.Create(["A.esm", "B.esm"], false, p => p == "A.esm" ? [] : ["A.esm"]);
    private static LoadOrderRecordVersion V(string plugin, uint id, string signature, uint flags = 0) => new(plugin, plugin, id, id, signature, null, flags, 24);
}
