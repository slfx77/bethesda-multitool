using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Semantic;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Phase 4 unit coverage for <see cref="CrossDumpSourceProjector" />. Constructs synthetic
///     <see cref="SemanticSource" />s and asserts the projection's shape, skeletons, observations,
///     and pass-A report set. NPC/Key/Container are explicitly excluded from pass-A and stashed
///     on <see cref="LateEnrichmentRecords" /> for pass-B.
/// </summary>
public class CrossDumpSourceProjectorTests
{
    [Fact]
    public void Project_copies_cell_fields_into_skeleton_and_preserves_placed_object_refs()
    {
        var cell = new CellRecord
        {
            FormId = 0x00010001,
            EditorId = "GoodspringsCell",
            FullName = "Goodsprings",
            WorldspaceFormId = 0x000DA726,
            GridX = 1,
            GridY = -1,
            Flags = 0x02, // HasWater
            IsVirtual = false,
            IsPersistentCell = false,
            IsUnresolvedBucket = false,
            HasPersistentObjects = true,
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0x00020001,
                    BaseFormId = 0x00030001,
                    RecordType = "REFR",
                    LockKeyFormId = 0x00040001
                }
            ]
        };

        var projection = Project(BuildSource(cells: [cell]));

        var skeleton = Assert.Single(projection.CellSkeletons);
        Assert.Equal(cell.FormId, skeleton.FormId);
        Assert.Equal(cell.EditorId, skeleton.EditorId);
        Assert.Equal(cell.FullName, skeleton.FullName);
        Assert.Equal(cell.WorldspaceFormId, skeleton.WorldspaceFormId);
        Assert.Equal(cell.GridX, skeleton.GridX);
        Assert.Equal(cell.GridY, skeleton.GridY);
        Assert.Equal(cell.Flags, skeleton.Flags);
        Assert.True(skeleton.HasWater);
        Assert.False(skeleton.IsInterior);
        Assert.True(skeleton.HasPersistentObjects);

        var placed = Assert.Single(skeleton.PlacedObjects);
        Assert.Equal(0x00020001u, placed.FormId);
        Assert.Equal(0x00030001u, placed.BaseFormId);
        Assert.Equal("REFR", placed.RecordType);
        Assert.Equal(0x00040001u, placed.LockKeyFormId);
        Assert.Same(cell.PlacedObjects[0], placed.Ref);
    }

    [Fact]
    public void Project_emits_npc_key_container_skeletons_and_stashes_records_for_pass_b()
    {
        var npc = new NpcRecord { FormId = 0x00050001, EditorId = "TestNpc" };
        var key = new KeyRecord { FormId = 0x00050002, EditorId = "TestKey" };
        var container = new ContainerRecord { FormId = 0x00050003, EditorId = "TestContainer" };

        var projection = Project(BuildSource(npcs: [npc], keys: [key], containers: [container]));

        Assert.Equal(0x00050001u, Assert.Single(projection.NpcSkeletons).FormId);
        Assert.Equal(0x00050002u, Assert.Single(projection.KeySkeletons).FormId);
        Assert.Equal(0x00050003u, Assert.Single(projection.ContainerSkeletons).FormId);

        Assert.NotNull(projection.LateEnrichment);
        Assert.Same(npc, Assert.Single(projection.LateEnrichment!.Npcs));
        Assert.Same(key, Assert.Single(projection.LateEnrichment.Keys));
        Assert.Same(container, Assert.Single(projection.LateEnrichment.Containers));

        // Pass A excludes NPC/Key/Container so their reports are NOT in ReportsByType yet.
        Assert.False(projection.ReportsByType.ContainsKey("NPC"));
        Assert.False(projection.ReportsByType.ContainsKey("Key"));
        Assert.False(projection.ReportsByType.ContainsKey("Container"));
    }

    [Fact]
    public void Project_dedupes_script_referenced_objects()
    {
        var script = new ScriptRecord
        {
            FormId = 0x00060001,
            EditorId = "TestScript",
            ReferencedObjects = [0x1u, 0x2u, 0x1u, 0x3u, 0x2u]
        };

        var projection = Project(BuildSource(scripts: [script]));

        var skeleton = Assert.Single(projection.ScriptSkeletons);
        Assert.Equal(new uint[] { 0x1u, 0x2u, 0x3u }, skeleton.ReferencedObjects);
    }

    [Fact]
    public void Project_captures_worldspace_observation_in_load_order()
    {
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x000ECAC5,
            EditorId = "CampMcCarranWorld",
            FullName = "Camp McCarran Tarmac"
        };

        var projection = Project(BuildSource(worldspaces: [worldspace]));

        var observation = Assert.Single(projection.WorldspaceObservations);
        Assert.Equal(0x000ECAC5u, observation.FormId);
        Assert.Equal("CampMcCarranWorld", observation.EditorId);
        Assert.Equal("Camp McCarran Tarmac", observation.DisplayName);

        // Direct lookup dict mirrors the WorldspaceRecord identity.
        Assert.True(projection.WorldspaceNames.TryGetValue(0x000ECAC5u, out var entry));
        Assert.Equal("CampMcCarranWorld", entry.EditorId);
        Assert.Equal("Camp McCarran Tarmac", entry.FullName);
    }

    [Fact]
    public void Project_skips_unresolved_buckets_from_cell_group_observations()
    {
        var realCell = new CellRecord { FormId = 0x1u, GridX = 0, GridY = 0, WorldspaceFormId = 0x2u };
        var bucket = new CellRecord { FormId = 0xFE000001u, IsVirtual = true, IsUnresolvedBucket = true };

        var projection = Project(BuildSource(cells: [realCell, bucket]));

        var observation = Assert.Single(projection.CellGroupObservations);
        Assert.Equal(0x1u, observation.CellFormId);

        // The bucket cell still gets a CellSkeleton entry (canonicalizer needs to see it)
        // but no group observation.
        Assert.Equal(2, projection.CellSkeletons.Count);
    }

    [Fact]
    public void Project_captures_dialogue_observation_with_first_response_text()
    {
        var dialogue = new DialogueRecord
        {
            FormId = 0x00070001,
            EditorId = "TestDialogue",
            TopicFormId = 0x00080001,
            QuestFormId = 0x00090001,
            SpeakerFormId = 0x000A0001,
            PromptText = "First prompt",
            Responses =
            [
                new DialogueResponse { Text = "" },
                new DialogueResponse { Text = "First non-empty response" },
                new DialogueResponse { Text = "Second response" }
            ]
        };

        var projection = Project(BuildSource(dialogues: [dialogue]));

        Assert.True(projection.DialogueObservations.TryGetValue(0x00070001u, out var observation));
        Assert.Equal(0x00080001u, observation!.TopicFormId);
        Assert.Equal(0x00090001u, observation.QuestFormId);
        Assert.Equal(0x000A0001u, observation.SpeakerFormId);
        Assert.Equal("First prompt", observation.FirstPromptText);
        Assert.Equal("First non-empty response", observation.FirstResponseText);
    }

    [Fact]
    public void Project_aggregates_dialog_topic_search_text_from_dialogues()
    {
        var topic = new DialogTopicRecord
        {
            FormId = 0x00080001,
            EditorId = "TestTopic",
            FullName = "Test Topic",
            DummyPrompt = "fallback prompt"
        };
        var dialogue1 = new DialogueRecord
        {
            FormId = 0x00070001,
            TopicFormId = 0x00080001,
            PromptText = "Greeting prompt",
            Responses = [new DialogueResponse { Text = "Hello there" }]
        };
        var dialogue2 = new DialogueRecord
        {
            FormId = 0x00070002,
            TopicFormId = 0x00080001,
            Responses = [new DialogueResponse { Text = "Goodbye now" }]
        };

        var projection = Project(BuildSource(dialogTopics: [topic], dialogues: [dialogue1, dialogue2]));

        Assert.True(projection.DialogTopicObservations.TryGetValue(0x00080001u, out var observation));
        Assert.Equal("Test Topic", observation!.FullName);
        Assert.Equal("fallback prompt", observation.DummyPrompt);
        Assert.NotNull(observation.SearchText);
        Assert.Contains("Greeting prompt", observation.SearchText);
        Assert.Contains("Hello there", observation.SearchText);
        Assert.Contains("Goodbye now", observation.SearchText);
    }

    [Fact]
    public void Project_includes_weapon_in_pass_a_reports()
    {
        var weapon = new WeaponRecord
        {
            FormId = 0x000B0001,
            EditorId = "TestWeapon",
            FullName = "Test Weapon"
        };

        var projection = Project(BuildSource(weapons: [weapon]));

        Assert.True(projection.ReportsByType.TryGetValue("Weapon", out var weaponReports));
        Assert.True(weaponReports!.ContainsKey(0x000B0001u));
    }

    [Fact]
    public void Project_resolves_build_date_from_esm_or_file_timestamp()
    {
        var source = BuildSource(filePath: "test.esm");
        var projection = Project(source);

        // Non-DMP path falls through to EsmBuildDateExtractor or file timestamp depending
        // on whether the synthetic path exists on disk. Either way, DateSource is non-empty.
        Assert.False(string.IsNullOrEmpty(projection.DateSource));
        Assert.False(projection.IsDmp);
        Assert.Equal("test", projection.ShortName);
    }

    // ---------- Helpers ----------

    private static CrossDumpSourceProjection Project(SemanticSource source)
    {
        return CrossDumpSourceProjector.Project(source);
    }

    private static SemanticSource BuildSource(
        string filePath = "test.dmp",
        IReadOnlyList<CellRecord>? cells = null,
        IReadOnlyList<NpcRecord>? npcs = null,
        IReadOnlyList<KeyRecord>? keys = null,
        IReadOnlyList<ContainerRecord>? containers = null,
        IReadOnlyList<ScriptRecord>? scripts = null,
        IReadOnlyList<WorldspaceRecord>? worldspaces = null,
        IReadOnlyList<DialogueRecord>? dialogues = null,
        IReadOnlyList<DialogTopicRecord>? dialogTopics = null,
        IReadOnlyList<WeaponRecord>? weapons = null)
    {
        var records = new RecordCollection
        {
            Cells = (cells ?? []).ToList(),
            Npcs = (npcs ?? []).ToList(),
            Keys = (keys ?? []).ToList(),
            Containers = (containers ?? []).ToList(),
            Scripts = (scripts ?? []).ToList(),
            Worldspaces = (worldspaces ?? []).ToList(),
            Dialogues = (dialogues ?? []).ToList(),
            DialogTopics = (dialogTopics ?? []).ToList(),
            Weapons = (weapons ?? []).ToList()
        };

        var resolver = new FormIdResolver(
            new Dictionary<uint, string>(),
            new Dictionary<uint, string>(),
            new Dictionary<uint, uint>());

        return new SemanticSource
        {
            FilePath = filePath,
            FileType = filePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                ? AnalysisFileType.Minidump
                : AnalysisFileType.EsmFile,
            Records = records,
            Resolver = resolver
        };
    }
}
