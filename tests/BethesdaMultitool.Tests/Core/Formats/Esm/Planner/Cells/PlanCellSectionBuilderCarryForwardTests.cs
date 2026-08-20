using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Master-children carry-forward + override-merge emission in the planner cell writer.
///     Under the in-game-proven interior cell-ownership rule, overriding a CELL makes the
///     plugin own the cell's temporary children — the writer must re-emit uncovered master
///     temporaries (per merge mode) and emit DMP-captured master refs as merges that keep
///     proto positions.
/// </summary>
public sealed class PlanCellSectionBuilderCarryForwardTests
{
    private const uint CellId = 0x000ABCDE;
    private const uint MasterTempRefId = 0x000A1001; // temp REFR, base = STAT
    private const uint MasterDoorRefId = 0x000A1002; // persistent REFR (GroupType 8)
    private const uint MasterFurnRefId = 0x000A1005; // temp REFR, base = FURN
    private const uint MasterTempAchrId = 0x000A1007; // temp ACHR (GroupType 9), base = NPC_
    private const uint MasterTempAchr2Id = 0x000A1008; // temp ACHR (GroupType 9), uncaptured
    private const uint MasterPersistentAchrId = 0x000A1009; // persistent ACHR (GroupType 8)
    private const uint MasterStatBaseId = 0x000A2001;
    private const uint MasterFurnBaseId = 0x000A2002;
    private const uint MasterNpcBaseId = 0x000A3001;

    // A genuine NEW proto child so the interior cell clears the "no new content" stability
    // gate (an ESM interior override is only emitted when it carries new records the master
    // lacks). Its base is a master STAT so it passes base validation.
    private const uint KeeperNewRefId = 0x01000901;

    [Fact]
    public void PersistentOnly_Carries_Uncovered_Master_Temporaries_But_Not_Persistent_Children()
    {
        // DMP capture: only a persistent master-resident ref → PersistentOnly mode.
        var dmpCell = new CellRecord
        {
            FormId = CellId,
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = MasterDoorRefId, BaseFormId = MasterStatBaseId,
                    RecordType = "REFR", IsPersistent = true
                }
            ]
        };

        var newAchr = new PlacedReference
        {
            FormId = 0x01000801, BaseFormId = MasterNpcBaseId, RecordType = "ACHR", IsPersistent = true
        };

        var (section, stats) = BuildSection(
            dmpCell,
            [
                (MakeChildPlan("ACHR", RecordDisposition.New, 0x01000801, newAchr), 8),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterDoorRefId, dmpCell.PlacedObjects[0]), 8)
            ],
            [0x01000801u]);

        Assert.NotNull(section);
        var records = WalkChildRecords(section);

        // New ACHR emitted in persistent; master temp REFR carried into temporary;
        // the persistent master door is neither overridden (sparse-preserve) nor carried
        // (GroupType-8 children load globally regardless of ownership).
        Assert.Contains(records, r => r is { Signature: "ACHR", FormId: 0x01000801, GroupType: 8 });

        // A new record inside a Persistent Children GRUP must carry the persistent header
        // flag (0x400) — GRUP membership and flag must agree or xEdit refuses to save the
        // file ("needs to have it's Persistent flag set").
        var newAchrRecord = Assert.Single(records, r => r.FormId == 0x01000801);
        Assert.Equal(0x400u, newAchrRecord.Flags & 0x400u);
        Assert.Contains(records, r => r.Signature == "REFR" && r.FormId == MasterTempRefId && r.GroupType == 9);
        Assert.DoesNotContain(records, r => r.FormId == MasterDoorRefId);
        Assert.Equal(1, stats.DropReasonCounts["refr.sparse-cell-master-preserved"]);
    }

    [Fact]
    public void Override_Child_Merges_Proto_Position_Onto_Master_Record()
    {
        var captured = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false,
            X = 123.5f, Y = -42.0f, Z = 7.0f
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [captured] };

        var (section, _) = BuildSection(
            dmpCell,
            [
                KeeperNewChild(),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, captured), 9)
            ]);

        Assert.NotNull(section);
        var records = WalkChildRecords(section);
        var emitted = Assert.Single(records, r => r.FormId == MasterTempRefId);
        Assert.Equal("REFR", emitted.Signature);
        Assert.Equal(9, emitted.GroupType); // Routed to master's original bucket.

        var data = FindSubrecord(section!, emitted.RecordOffset, "DATA");
        Assert.NotNull(data);
        Assert.Equal(123.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0, 4)));
    }

    [Fact]
    public void CarryForward_Alone_Never_Unsuppresses_An_Itm_Override()
    {
        // Sparse capture whose only child is a dropped override: zero genuine children →
        // the whole cell must be suppressed, master temporaries untouched.
        var capturedDoor = new PlacedReference
        {
            FormId = MasterDoorRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = true
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [capturedDoor] };

        var (section, stats) = BuildSection(
            dmpCell,
            [(MakeChildPlan("REFR", RecordDisposition.Override, MasterDoorRefId, capturedDoor), 8)]);

        Assert.Null(section);
        Assert.Equal(1, stats.DropReasonCounts["cell.itm-override-suppressed"]);
    }

    [Fact]
    public void Override_With_Mismatched_Master_Parent_Is_Dropped()
    {
        var captured = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [captured] };

        var (section, stats) = BuildSection(
            dmpCell,
            [(MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, captured), 9)],
            masterParentOfTempRef: 0x000FFFFF); // Master files the ref under a different cell.

        // The ref does not survive and the cell therefore emits nothing. Retirement Stage H1
        // moved the per-ref decision to plan time, so the precise reason code is asserted at
        // its source in CellChildVerdictPlannerTests; here we pin the observable outcome.
        Assert.Null(section);
        Assert.NotEmpty(stats.DropReasonCounts);
    }

    [Fact]
    public void LoadedReplacement_Omits_CapturedType_Masters_And_Carries_UncapturedType()
    {
        // DMP authoritatively captured a STAT-based ref → uncovered master STAT refs are
        // omitted (deletion by omission); the FURN-based master ref (type not captured)
        // is carried.
        var captured = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [captured] };

        var (section, _) = BuildSection(
            dmpCell,
            [
                KeeperNewChild(),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, captured), 9)
            ],
            includeExtraStatTemp: true);

        Assert.NotNull(section);
        var records = WalkChildRecords(section);
        Assert.Contains(records, r => r.FormId == MasterTempRefId && !r.Deleted); // Covered override.
        Assert.Contains(records, r => r.FormId == MasterFurnRefId && !r.Deleted); // Carried (FURN uncaptured).
        // STAT-typed master temp with a captured-type base: not carried — removed via the
        // undelete-and-disable pattern (Initially Disabled override, NOT a deleted-flag
        // stub: deleted refs are FNV's classic referenced-form crash source).
        var removed = Assert.Single(records, r => r.FormId == 0x000A1006);
        Assert.False(removed.Deleted);
        Assert.Equal(0x800u, removed.Flags & 0x800u);
    }

    [Fact]
    public void Temp_Actor_Overrides_Are_Suppressed_And_Master_Actors_Left_Untouched()
    {
        // ESM semantics: re-emitting a master TEMPORARY actor (override or carry) leaves
        // its form skipped by the attach-time temp stream and never initialized — the AI
        // package then dereferences the raw XLKR FormID (Gomorrah01 patrol AV). The writer
        // must emit NOTHING for master temp actors: no override, no carry, no tombstone.
        var capturedRefr = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false
        };
        var capturedActor = new PlacedReference
        {
            FormId = MasterTempAchrId, BaseFormId = MasterNpcBaseId,
            RecordType = "ACHR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [capturedRefr, capturedActor] };

        var (section, stats) = BuildSection(
            dmpCell,
            [
                KeeperNewChild(),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, capturedRefr), 9),
                (MakeChildPlan("ACHR", RecordDisposition.Override, MasterTempAchrId, capturedActor), 9)
            ],
            includeTempActors: true);

        Assert.NotNull(section);
        var records = WalkChildRecords(section);

        // The genuine REFR override still anchors the cell.
        Assert.Contains(records, r => r.FormId == MasterTempRefId && !r.Deleted);
        // Captured temp actor: override suppressed, and being covered it is neither
        // carried nor tombstoned.
        Assert.DoesNotContain(records, r => r.FormId == MasterTempAchrId);
        // Uncaptured temp actor: excluded from carry-forward AND tombstone synthesis.
        Assert.DoesNotContain(records, r => r.FormId == MasterTempAchr2Id);
        Assert.Equal(1, stats.DropReasonCounts["actor.temp-override-suppressed-esm"]);
    }

    [Fact]
    public void Persistent_Actor_Override_Still_Emits()
    {
        // Persistent actor overrides are shipped-DLC-normal and load through the startup
        // path — only TEMPORARY actors are suppressed.
        var capturedRefr = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false
        };
        var capturedActor = new PlacedReference
        {
            FormId = MasterPersistentAchrId, BaseFormId = MasterNpcBaseId,
            RecordType = "ACHR", IsPersistent = true
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [capturedRefr, capturedActor] };

        var (section, _) = BuildSection(
            dmpCell,
            [
                KeeperNewChild(),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, capturedRefr), 9),
                (MakeChildPlan("ACHR", RecordDisposition.Override, MasterPersistentAchrId, capturedActor), 8)
            ],
            includeTempActors: true);

        Assert.NotNull(section);
        var records = WalkChildRecords(section);
        Assert.Contains(records, r => r is { Signature: "ACHR" } && r.FormId == MasterPersistentAchrId
                                                                 && r.GroupType == 8 && !r.Deleted);
    }

    [Fact]
    public void Interior_Cell_Override_Is_Filed_At_Engine_Formula_Block_And_Subblock()
    {
        // Interior CELL overrides must be filed at the engine's FormID-derived block/sub-block
        // (block = fid%10, sub = (fid%100)/10), NOT the master's flat labels — otherwise the
        // engine's scan-fallback descend predicate skips our GRUPs and never finds the cell
        // (temp children never stream → crash / empty interior). CellId 0x000ABCDE →
        // low 0x0ABCDE = 703710 → block 0, sub 1.
        const uint low = CellId & 0xFFFFFFu;
        var expectedBlock = low % 10; // 0
        var expectedSub = low % 100 / 10; // 1

        var captured = new PlacedReference
        {
            FormId = MasterTempRefId, BaseFormId = MasterStatBaseId,
            RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [captured] };

        var (section, _) = BuildSection(
            dmpCell,
            [
                KeeperNewChild(),
                (MakeChildPlan("REFR", RecordDisposition.Override, MasterTempRefId, captured), 9)
            ]);

        Assert.NotNull(section);

        // Walk to the interior block (type 2) and sub-block (type 3) GRUP labels.
        var (block, sub) = FindInteriorBlockSubLabels(section!);
        Assert.Equal(expectedBlock, block);
        Assert.Equal(expectedSub, sub);
    }

    private static (uint Block, uint Sub) FindInteriorBlockSubLabels(byte[] section)
    {
        uint block = uint.MaxValue, sub = uint.MaxValue;
        var pos = 0;
        while (pos + 24 <= section.Length)
        {
            var sig = Encoding.ASCII.GetString(section, pos, 4);
            if (sig == "GRUP")
            {
                var label = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 8, 4));
                var type = BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(pos + 12, 4));
                if (type == 2)
                {
                    block = label;
                }
                else if (type == 3)
                {
                    sub = label;
                }

                pos += 24;
                continue;
            }

            pos += 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 4, 4));
        }

        return (block, sub);
    }

    private static (RecordPlan Plan, int Bucket) KeeperNewChild()
    {
        return (MakeChildPlan("REFR", RecordDisposition.New, KeeperNewRefId, new PlacedReference
        {
            FormId = KeeperNewRefId, BaseFormId = MasterStatBaseId, RecordType = "REFR", IsPersistent = false
        }), 9);
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static (byte[]? Section, ConversionPipelineStats Stats) BuildSection(
        CellRecord dmpCell,
        (RecordPlan Plan, int Bucket)[] children,
        uint[]? emittedFormIds = null,
        uint masterParentOfTempRef = CellId,
        bool includeExtraStatTemp = false,
        bool includeTempActors = false)
    {
        var masterCell = MakeMasterRecord("CELL", CellId) with
        {
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = "Cell\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "DATA", Data = [0x01] }, // interior flag byte
                new ParsedSubrecord { Signature = "XCLL", Data = new byte[40] } // interior lighting
            ]
        };
        var masterTempRef = MakeMasterRefRecord(MasterTempRefId, MasterStatBaseId);
        var masterDoorRef = MakeMasterRefRecord(MasterDoorRefId, MasterStatBaseId);
        var masterFurnRef = MakeMasterRefRecord(MasterFurnRefId, MasterFurnBaseId);
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [CellId] = masterCell,
            [MasterTempRefId] = masterTempRef,
            [MasterDoorRefId] = masterDoorRef,
            [MasterFurnRefId] = masterFurnRef,
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId),
            [MasterFurnBaseId] = MakeMasterRecord("FURN", MasterFurnBaseId),
            [MasterNpcBaseId] = MakeMasterRecord("NPC_", MasterNpcBaseId)
        };

        var childLocations = new Dictionary<uint, MasterChildLocation>
        {
            [MasterTempRefId] = new(masterParentOfTempRef, 9, "REFR"),
            [MasterDoorRefId] = new(CellId, 8, "REFR"),
            [MasterFurnRefId] = new(CellId, 9, "REFR")
        };
        var refsByCell = new Dictionary<uint, List<uint>>
        {
            [CellId] = [MasterTempRefId, MasterDoorRefId, MasterFurnRefId]
        };

        if (includeExtraStatTemp)
        {
            masterByFormId[0x000A1006] = MakeMasterRefRecord(0x000A1006, MasterStatBaseId);
            childLocations[0x000A1006] = new MasterChildLocation(CellId, 9, "REFR");
            refsByCell[CellId].Add(0x000A1006);
        }

        if (includeTempActors)
        {
            masterByFormId[MasterTempAchrId] = MakeMasterRefRecord(MasterTempAchrId, MasterNpcBaseId, "ACHR");
            masterByFormId[MasterTempAchr2Id] = MakeMasterRefRecord(MasterTempAchr2Id, MasterNpcBaseId, "ACHR");
            masterByFormId[MasterPersistentAchrId] =
                MakeMasterRefRecord(MasterPersistentAchrId, MasterNpcBaseId, "ACHR");
            childLocations[MasterTempAchrId] = new MasterChildLocation(CellId, 9, "ACHR");
            childLocations[MasterTempAchr2Id] = new MasterChildLocation(CellId, 9, "ACHR");
            childLocations[MasterPersistentAchrId] = new MasterChildLocation(CellId, 8, "ACHR");
            refsByCell[CellId].AddRange([MasterTempAchrId, MasterTempAchr2Id, MasterPersistentAchrId]);
        }

        var masterIndex = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId,
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = childLocations,
            RefToCell = childLocations.ToDictionary(kv => kv.Key, kv => kv.Value.CellFormId),
            RefsByCell = refsByCell,
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = []
        };

        var context = MakeInteriorContext(CellId);
        var cellPlan = new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.Override,
                FormId = CellId,
                Model = dmpCell,
                Master = masterCell,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
            },
            Context = context,
            PersistentChildren = [.. children.Where(c => c.Bucket == 8).Select(c => c.Plan)],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [.. children.Where(c => c.Bucket == 9).Select(c => c.Plan)]
        };

        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = emittedFormIds is null
                ? ImmutableHashSet<uint>.Empty
                : [.. emittedFormIds],
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata { NextObjectId = 0x800, PlannerCoverage = ImmutableHashSet<string>.Empty },
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cellPlan)
        };

        var stats = new ConversionPipelineStats();
        var planSettled = CellPlanTestHarness.Settle(plan, masterByFormId, masterIndex, new PluginBuildOptions());
        var section = PlanCellSectionBuilder.BuildCellSection(planSettled, masterByFormId, new PluginBuildOptions(),
            stats, masterIndex);
        return (section, stats);
    }

    private static RecordPlan MakeChildPlan(
        string type, RecordDisposition disposition, uint formId, PlacedReference model)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static ParsedMainRecord MakeMasterRecord(string signature, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature, DataSize = 0, Flags = 0, FormId = formId,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0
        };
    }

    private static ParsedMainRecord MakeMasterRefRecord(uint formId, uint baseFormId, string signature = "REFR")
    {
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, baseFormId);
        var data = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), 1.0f);
        return MakeMasterRecord(signature, formId) with
        {
            Subrecords =
            [
                new ParsedSubrecord { Signature = "NAME", Data = name },
                new ParsedSubrecord { Signature = "DATA", Data = data }
            ]
        };
    }

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId)
    {
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3,
            BlockLabel = [1, 0, 0, 0],
            SubblockLabel = [2, 0, 0, 0]
        };
    }

    private static List<ChildRecord> WalkChildRecords(byte[]? section)
    {
        var result = new List<ChildRecord>();
        if (section is null)
        {
            return result;
        }

        Walk(section, 0, section.Length, 0, result);
        return result;
    }

    private static void Walk(byte[] data, int start, int end, int currentChildGroupType, List<ChildRecord> result)
    {
        var pos = start;
        while (pos + 24 <= end)
        {
            var sig = Encoding.ASCII.GetString(data, pos, 4);
            if (sig == "GRUP")
            {
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
                var groupType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 12, 4));
                var childType = groupType is 8 or 9 or 10 ? groupType : currentChildGroupType;
                Walk(data, pos + 24, pos + size, childType, result);
                pos += size;
            }
            else
            {
                var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8, 4));
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12, 4));
                if (sig != "CELL")
                {
                    result.Add(new ChildRecord(
                        sig, formId, currentChildGroupType, pos, (flags & 0x20) != 0, flags));
                }

                pos += 24 + dataSize;
            }
        }
    }

    private static byte[]? FindSubrecord(byte[] section, int recordOffset, string signature)
    {
        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(recordOffset + 4, 4));
        var pos = recordOffset + 24;
        var end = pos + dataSize;
        while (pos + 6 <= end)
        {
            var sig = Encoding.ASCII.GetString(section, pos, 4);
            var len = BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(pos + 4, 2));
            if (sig == signature)
            {
                return section.AsSpan(pos + 6, len).ToArray();
            }

            pos += 6 + len;
        }

        return null;
    }

    // ---- section walker ------------------------------------------------------------

    private sealed record ChildRecord(
        string Signature,
        uint FormId,
        int GroupType,
        int RecordOffset,
        bool Deleted,
        uint Flags);
}