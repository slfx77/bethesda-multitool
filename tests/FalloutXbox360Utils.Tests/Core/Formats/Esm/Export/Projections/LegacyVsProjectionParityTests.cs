using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Semantic;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Phase 8 parity test — runs both the legacy <see cref="CrossDumpAggregator.Aggregate" />
///     path and the new projection-based path on the same synthetic fixture, then asserts
///     the resulting <see cref="CrossDumpRecordIndex" /> matches at the
///     (type, formId, dumpIdx) → <see cref="RecordReport" /> level plus group labels and
///     metadata. Catches silent drift introduced by the wire flip the planner warned about
///     as the streaming refactor's #1 risk.
/// </summary>
public class LegacyVsProjectionParityTests
{
    [Fact]
    public void Two_source_fixture_produces_identical_indexes_through_both_pipelines()
    {
        // Build the same two SemanticSources twice (once per pipeline) to avoid sharing
        // mutable RecordCollection state. The legacy aggregator mutates RecordCollection
        // lists when releaseInputRecords=true and the projection pipeline reads the records
        // during Project(); using fresh copies keeps the two paths isolated.
        var legacyInputs = BuildTwoSourceFixture();
        var legacyIndex = CrossDumpAggregator.Aggregate(
            legacyInputs.Select(s => (s.FilePath, s.Records, s.Resolver, s.MinidumpInfo)).ToList(),
            allowedTypes: null,
            releaseInputRecords: false);

        var projectionInputs = BuildTwoSourceFixture();
        var projections = projectionInputs.Select(CrossDumpSourceProjector.Project).ToList();
        var virtualCanon = VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projections.Select(p => p.CellSkeletons));
        var npcPlacements = CrossDumpPlacementIndexBuilder.BuildNpcPlacementIndexes(projections, virtualCanon);
        var npcScriptRefs = CrossDumpPlacementIndexBuilder.BuildNpcScriptReferenceIndexes(projections);
        var keyDoors = CrossDumpPlacementIndexBuilder.BuildKeyLockedDoorIndexes(projections, virtualCanon);
        var containerPlacements = CrossDumpPlacementIndexBuilder.BuildContainerPlacementIndexes(
            projections, virtualCanon);
        CrossDumpProjectionAggregator.BuildLatePassReports(
            projections, npcPlacements, npcScriptRefs, keyDoors, containerPlacements);
        var newIndex = CrossDumpProjectionAggregator.AggregateFromProjections(
            projections, virtualCanon, allowedTypes: null);

        AssertIndexesMatch(legacyIndex, newIndex);
    }

    private static void AssertIndexesMatch(CrossDumpRecordIndex legacy, CrossDumpRecordIndex projection)
    {
        // Same chronological dump ordering. The streaming pipeline sorts by BuildDateUtc;
        // legacy Aggregate does the same.
        Assert.Equal(legacy.Dumps.Count, projection.Dumps.Count);
        for (var i = 0; i < legacy.Dumps.Count; i++)
        {
            Assert.Equal(legacy.Dumps[i].ShortName, projection.Dumps[i].ShortName);
            Assert.Equal(legacy.Dumps[i].FileName, projection.Dumps[i].FileName);
            Assert.Equal(legacy.Dumps[i].IsDmp, projection.Dumps[i].IsDmp);
            Assert.Equal(legacy.Dumps[i].FileDate, projection.Dumps[i].FileDate);
        }

        // Same record types covered.
        var legacyTypes = legacy.StructuredRecords.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var projectionTypes = projection.StructuredRecords.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(legacyTypes, projectionTypes);

        // Same FormIDs per type, same per-dump RecordReport identity.
        foreach (var typeName in legacyTypes)
        {
            var legacyFormIds = legacy.StructuredRecords[typeName].Keys.OrderBy(f => f).ToList();
            var projectionFormIds = projection.StructuredRecords[typeName].Keys.OrderBy(f => f).ToList();
            Assert.Equal(legacyFormIds, projectionFormIds);

            foreach (var formId in legacyFormIds)
            {
                var legacyDumpMap = legacy.StructuredRecords[typeName][formId];
                var projectionDumpMap = projection.StructuredRecords[typeName][formId];
                Assert.Equal(legacyDumpMap.Keys.OrderBy(d => d), projectionDumpMap.Keys.OrderBy(d => d));

                foreach (var dumpIdx in legacyDumpMap.Keys)
                {
                    var legacyReport = legacyDumpMap[dumpIdx];
                    var projectionReport = projectionDumpMap[dumpIdx];
                    Assert.True(
                        RecordReportComparer.Equals(legacyReport, projectionReport),
                        $"RecordReport diverged for type={typeName}, formId=0x{formId:X8}, dumpIdx={dumpIdx}");
                }
            }
        }

        // Same group labels (Cell, Dialogue_NPC, Dialogue_Quest).
        var legacyGroupTypes = legacy.RecordGroups.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var projectionGroupTypes = projection.RecordGroups.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(legacyGroupTypes, projectionGroupTypes);

        foreach (var groupType in legacyGroupTypes)
        {
            var legacyGroups = legacy.RecordGroups[groupType];
            var projectionGroups = projection.RecordGroups[groupType];
            Assert.Equal(legacyGroups.Keys.OrderBy(f => f), projectionGroups.Keys.OrderBy(f => f));

            foreach (var formId in legacyGroups.Keys)
            {
                Assert.Equal(
                    legacyGroups[formId],
                    projectionGroups[formId]);
            }
        }

        // Same per-record metadata (e.g. dialogue questFormId / topicName etc.).
        var legacyMetaTypes = legacy.RecordMetadata.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var projectionMetaTypes = projection.RecordMetadata.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(legacyMetaTypes, projectionMetaTypes);

        foreach (var metaType in legacyMetaTypes)
        {
            var legacyMeta = legacy.RecordMetadata[metaType];
            var projectionMeta = projection.RecordMetadata[metaType];
            Assert.Equal(legacyMeta.Keys.OrderBy(f => f), projectionMeta.Keys.OrderBy(f => f));

            foreach (var formId in legacyMeta.Keys)
            {
                var legacyDict = legacyMeta[formId];
                var projectionDict = projectionMeta[formId];
                Assert.Equal(
                    legacyDict.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                    projectionDict.OrderBy(kv => kv.Key, StringComparer.Ordinal));
            }
        }

        // Same cell grid coords.
        Assert.Equal(
            legacy.CellGridCoords.OrderBy(kv => kv.Key),
            projection.CellGridCoords.OrderBy(kv => kv.Key));
    }

    // ---------- Synthetic fixture builder ----------

    /// <summary>
    ///     Two sources sharing one common record (Weapon FormID 0x000B0001, with a different
    ///     EditorID per source) plus a worldspace whose display name changes between the two
    ///     sources (Camp McCarran Tarmac → Camp McCarran). The earlier source is a DMP, the
    ///     later is an ESM — exercises ESM-authority gate for cell groups too.
    /// </summary>
    private static List<SemanticSource> BuildTwoSourceFixture()
    {
        // Common identifiers used across both sources.
        const uint sharedWeaponFormId = 0x000B0001;
        const uint sharedWorldspaceFormId = 0x000ECAC5;
        const uint sharedCellFormId = 0x00010001;
        const string sharedWorldspaceEditorId = "CampMcCarranWorld";

        // Source 1: earlier DMP — worldspace name is "Camp McCarran Tarmac".
        var s1Worldspace = new WorldspaceRecord
        {
            FormId = sharedWorldspaceFormId,
            EditorId = sharedWorldspaceEditorId,
            FullName = "Camp McCarran Tarmac"
        };
        var s1Cell = new CellRecord
        {
            FormId = sharedCellFormId,
            EditorId = "FreesideDeadbeatAlley",
            WorldspaceFormId = sharedWorldspaceFormId,
            GridX = 1,
            GridY = -1,
            Flags = 0x00
        };
        var s1Weapon = new WeaponRecord
        {
            FormId = sharedWeaponFormId,
            EditorId = "TestWeapon",
            FullName = "Test Weapon"
        };
        var s1Npc = new NpcRecord
        {
            FormId = 0x00050001,
            EditorId = "TestNpc",
            FullName = "Test NPC"
        };
        var s1Quest = new QuestRecord
        {
            FormId = 0x00060001,
            EditorId = "TestQuest",
            FullName = "Test Quest"
        };

        var s1 = BuildSource(
            filePath: "early.dmp",
            isDmp: true,
            worldspaces: [s1Worldspace],
            cells: [s1Cell],
            weapons: [s1Weapon],
            npcs: [s1Npc],
            quests: [s1Quest]);

        // Source 2: later ESM — worldspace renamed to "Camp McCarran". Same cell + weapon
        // FormIDs. The weapon's EditorID is renamed too (TestWeapon → TestWeaponV2) to
        // exercise name-history tracking in the cross-dump report.
        var s2Worldspace = new WorldspaceRecord
        {
            FormId = sharedWorldspaceFormId,
            EditorId = sharedWorldspaceEditorId,
            FullName = "Camp McCarran"
        };
        var s2Cell = new CellRecord
        {
            FormId = sharedCellFormId,
            EditorId = "FreesideDeadbeatAlleyOLD", // renamed cell
            WorldspaceFormId = sharedWorldspaceFormId,
            GridX = 1,
            GridY = -1,
            Flags = 0x00
        };
        var s2Weapon = new WeaponRecord
        {
            FormId = sharedWeaponFormId,
            EditorId = "TestWeaponV2",
            FullName = "Test Weapon V2"
        };
        var s2Quest = new QuestRecord
        {
            FormId = 0x00060001,
            EditorId = "TestQuest",
            FullName = "Test Quest (renamed)"
        };

        var s2 = BuildSource(
            filePath: "later.esm",
            isDmp: false,
            worldspaces: [s2Worldspace],
            cells: [s2Cell],
            weapons: [s2Weapon],
            quests: [s2Quest]);

        return [s1, s2];
    }

    private static SemanticSource BuildSource(
        string filePath,
        bool isDmp,
        IReadOnlyList<WorldspaceRecord>? worldspaces = null,
        IReadOnlyList<CellRecord>? cells = null,
        IReadOnlyList<WeaponRecord>? weapons = null,
        IReadOnlyList<NpcRecord>? npcs = null,
        IReadOnlyList<QuestRecord>? quests = null)
    {
        var records = new RecordCollection
        {
            Worldspaces = (worldspaces ?? []).ToList(),
            Cells = (cells ?? []).ToList(),
            Weapons = (weapons ?? []).ToList(),
            Npcs = (npcs ?? []).ToList(),
            Quests = (quests ?? []).ToList()
        };

        // FormIdResolver seeded with EditorID + DisplayName entries so report builders can
        // resolve cross-references (matters for the projection-path FormIdResolver lookups).
        var editorIds = new Dictionary<uint, string>();
        var displayNames = new Dictionary<uint, string>();
        foreach (var w in records.Worldspaces)
        {
            if (w.EditorId != null) editorIds[w.FormId] = w.EditorId;
            if (w.FullName != null) displayNames[w.FormId] = w.FullName;
        }

        foreach (var c in records.Cells)
        {
            if (c.EditorId != null) editorIds[c.FormId] = c.EditorId;
        }

        foreach (var w in records.Weapons)
        {
            if (w.EditorId != null) editorIds[w.FormId] = w.EditorId;
            if (w.FullName != null) displayNames[w.FormId] = w.FullName;
        }

        foreach (var n in records.Npcs)
        {
            if (n.EditorId != null) editorIds[n.FormId] = n.EditorId;
            if (n.FullName != null) displayNames[n.FormId] = n.FullName;
        }

        foreach (var q in records.Quests)
        {
            if (q.EditorId != null) editorIds[q.FormId] = q.EditorId;
            if (q.FullName != null) displayNames[q.FormId] = q.FullName;
        }

        var resolver = new FormIdResolver(editorIds, displayNames, new Dictionary<uint, uint>());

        return new SemanticSource
        {
            FilePath = filePath,
            FileType = isDmp ? AnalysisFileType.Minidump : AnalysisFileType.EsmFile,
            Records = records,
            Resolver = resolver
        };
    }
}
