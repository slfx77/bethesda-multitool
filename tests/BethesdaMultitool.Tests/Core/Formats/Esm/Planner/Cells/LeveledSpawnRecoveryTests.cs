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
using Xunit.Sdk;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Leveled-spawn recovery in the planner cell writer: a placed actor (ACHR/ACRE) whose base
///     is a runtime-dynamic clone (0xFF) is re-pointed — under
///     <see cref="PluginBuildOptions.RecoverLeveledSpawnActors" /> — to its captured
///     <c>ExtraLeveledCreature</c> base (<c>OriginalBase</c> preferred, else <c>Template</c>),
///     accepted only when it resolves to a type-compatible NPC_/CREA (master or captured-proto).
///     Otherwise the actor still drops as <c>refr.dangling-base</c>.
/// </summary>
public sealed class LeveledSpawnRecoveryTests
{
    private const uint CellId = 0x000ABCDE; // interior
    private const uint MasterStatBaseId = 0x000A2001; // keeper-ref base
    private const uint MasterNpcBaseId = 0x000A3001; // ACHR recovery target (master NPC_)
    private const uint MasterCreaBaseId = 0x000A3002; // ACRE recovery target (master CREA)
    private const uint MasterLvlnBaseId = 0x000A3003; // leveled list (type-incompatible base)
    private const uint KeeperNewRefId = 0x01000901; // clears the interior "no new content" gate
    private const uint ActorFormId = 0x01000AAA; // the re-populated actor's own FormID
    private const uint RuntimeCloneBase = 0xFF000A48; // the 0xFF base the planner can't resolve

    private const uint ProtoNpcSource = 0x0011EC2A; // captured-proto NPC_ source FormID
    private const uint ProtoNpcEmitted = 0x01004330; // its emitted (plugin) FormID

    [Fact]
    public void MasterNpc_Template_Recovers_Via_Template_When_OriginalBase_Is_Leveled()
    {
        // OriginalBase = master LVLN (xEdit-invalid ACHR base → skipped), Template = master NPC_.
        var actor = MakeActor("ACHR", MasterLvlnBaseId, MasterNpcBaseId);

        var (section, stats) = BuildSection(actor, true);

        var name = FindActorNameBase(section, ActorFormId);
        Assert.Equal(MasterNpcBaseId, name); // Template won; LVLN OriginalBase rejected.
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("refr.dangling-base"));
    }

    [Fact]
    public void MasterNpc_OriginalBase_Is_Preferred_Over_Template()
    {
        // OriginalBase = master NPC_ (type-compatible) → wins; Template ignored.
        var actor = MakeActor("ACHR", MasterNpcBaseId, MasterCreaBaseId);

        var (section, stats) = BuildSection(actor, true);

        Assert.Equal(MasterNpcBaseId, FindActorNameBase(section, ActorFormId));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
    }

    [Fact]
    public void CapturedProto_Npc_Base_Recovers_Via_SourceToEmitted()
    {
        // OriginalBase = a captured-proto NPC_ emitted as new; NAME must land on the emitted FormID.
        var actor = MakeActor("ACHR", ProtoNpcSource, null);

        var (section, stats) = BuildSection(
            actor, true,
            ImmutableDictionary<uint, uint>.Empty.Add(ProtoNpcSource, ProtoNpcEmitted),
            [ProtoNpcEmitted],
            new Dictionary<uint, string> { [ProtoNpcSource] = "NPC_" });

        Assert.Equal(ProtoNpcEmitted, FindActorNameBase(section, ActorFormId));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
    }

    [Fact]
    public void CapturedProto_WrongType_Base_Is_Rejected_And_Still_Drops()
    {
        // Emitted proto base is a STAT, not an NPC_ — the DmpBaseTypes gate must reject it.
        var actor = MakeActor("ACHR", ProtoNpcSource, null);

        var (section, stats) = BuildSection(
            actor, true,
            ImmutableDictionary<uint, uint>.Empty.Add(ProtoNpcSource, ProtoNpcEmitted),
            [ProtoNpcEmitted],
            new Dictionary<uint, string> { [ProtoNpcSource] = "STAT" });

        Assert.Null(FindActorRecord(section, ActorFormId));
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.dangling-base"));
    }

    [Fact]
    public void No_Leveled_Pointers_Still_Drops_As_Dangling()
    {
        var actor = MakeActor("ACHR", null, null);

        var (section, stats) = BuildSection(actor, true);

        Assert.Null(FindActorRecord(section, ActorFormId));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.dangling-base"));
    }

    [Fact]
    public void Flag_Off_Drops_Even_With_A_Valid_Master_Base()
    {
        var actor = MakeActor("ACHR", MasterNpcBaseId, null);

        var (section, stats) = BuildSection(actor, false);

        Assert.Null(FindActorRecord(section, ActorFormId));
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.dangling-base"));
    }

    [Fact]
    public void Refr_With_Runtime_Base_And_Leveled_Pointers_Is_Unaffected()
    {
        // Recovery is ACHR/ACRE-only; a REFR with a 0xFF base still dangles.
        var reffed = MakeActor("REFR", MasterNpcBaseId, null);

        var (section, stats) = BuildSection(reffed, true);

        Assert.Null(FindActorRecord(section, ActorFormId));
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.dangling-base"));
    }

    [Fact]
    public void Acre_Recovers_Via_Master_Crea_And_Skips_Leveled_OriginalBase()
    {
        // ACRE parity: OriginalBase = LVLN (skipped), Template = master CREA.
        var actor = MakeActor("ACRE", MasterLvlnBaseId, MasterCreaBaseId);

        var (section, stats) = BuildSection(actor, true);

        Assert.Equal(MasterCreaBaseId, FindActorNameBase(section, ActorFormId));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered"));
    }

    [Fact]
    public void Actor_Base_Type_Rule_Rejects_Leveled_Lists()
    {
        // Documents the design decision: ACHR/ACRE may not sit on a LVLN/LVLC base (xEdit-invalid),
        // which is exactly why recovery lands on the resolved NPC_/CREA, never the LVLN.
        Assert.True(ReferenceBaseRemapper.CanPlacedRecordUseBaseType("ACHR", "NPC_"));
        Assert.True(ReferenceBaseRemapper.CanPlacedRecordUseBaseType("ACRE", "CREA"));
        Assert.False(ReferenceBaseRemapper.CanPlacedRecordUseBaseType("ACHR", "LVLN"));
        Assert.False(ReferenceBaseRemapper.CanPlacedRecordUseBaseType("ACRE", "LVLC"));
        Assert.False(ReferenceBaseRemapper.CanPlacedRecordUseBaseType("ACHR", "LVLC"));
    }

    // ---- fixture plumbing ----------------------------------------------------------

    private static PlacedReference MakeActor(string recordType, uint? original, uint? template)
    {
        return new PlacedReference
        {
            FormId = ActorFormId,
            BaseFormId = RuntimeCloneBase,
            RecordType = recordType,
            IsPersistent = false,
            LeveledCreatureOriginalBaseFormId = original,
            LeveledCreatureTemplateFormId = template
        };
    }

    private static (byte[]? Section, ConversionPipelineStats Stats) BuildSection(
        PlacedReference actor,
        bool recover,
        ImmutableDictionary<uint, uint>? sourceToEmitted = null,
        uint[]? emittedFormIds = null,
        IReadOnlyDictionary<uint, string>? dmpBaseTypes = null)
    {
        var keeper = new PlacedReference
        {
            FormId = KeeperNewRefId, BaseFormId = MasterStatBaseId, RecordType = "REFR", IsPersistent = false
        };
        var dmpCell = new CellRecord { FormId = CellId, PlacedObjects = [keeper, actor] };

        var masterCell = MakeMasterRecord("CELL", CellId) with
        {
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = "Cell\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "DATA", Data = [0x01] }, // interior flag
                new ParsedSubrecord { Signature = "XCLL", Data = new byte[40] }
            ]
        };
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>
        {
            [CellId] = masterCell,
            [MasterStatBaseId] = MakeMasterRecord("STAT", MasterStatBaseId),
            [MasterNpcBaseId] = MakeMasterRecord("NPC_", MasterNpcBaseId),
            [MasterCreaBaseId] = MakeMasterRecord("CREA", MasterCreaBaseId),
            [MasterLvlnBaseId] = MakeMasterRecord("LVLN", MasterLvlnBaseId)
        };

        var masterIndex = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId,
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = [],
            RefToCell = [],
            RefsByCell = [],
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = []
        };

        var actorType = actor.RecordType;
        var cellPlan = new CellPlan
        {
            CellFormId = CellId,
            CellRecordPlan = MakeCellPlan(dmpCell, masterCell),
            Context = MakeInteriorContext(CellId),
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren =
            [
                MakeChildPlan("REFR", KeeperNewRefId, keeper),
                MakeChildPlan(actorType, ActorFormId, actor)
            ]
        };

        var plan = new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = sourceToEmitted ?? ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = emittedFormIds is null ? ImmutableHashSet<uint>.Empty : [.. emittedFormIds],
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata { NextObjectId = 0x800, PlannerCoverage = ImmutableHashSet<string>.Empty },
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(CellId, cellPlan)
        };

        var options = new PluginBuildOptions { RecoverLeveledSpawnActors = recover };
        var stats = new ConversionPipelineStats();
        var section = PlanCellSectionBuilder
            .BuildCellSectionCore(plan, masterByFormId, options, stats, masterIndex, dmpBaseTypes)
            .SectionBytes;
        return (section, stats);
    }

    private static RecordPlan MakeCellPlan(CellRecord dmpCell, ParsedMainRecord masterCell)
    {
        return new RecordPlan
        {
            Type = "CELL",
            Disposition = RecordDisposition.Override,
            FormId = CellId,
            Model = dmpCell,
            Master = masterCell,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static RecordPlan MakeChildPlan(string type, uint formId, PlacedReference model)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = RecordDisposition.New,
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

    // ---- section reader ------------------------------------------------------------

    /// <summary>Returns the NAME (base FormID) of the emitted actor record, or throws if absent.</summary>
    private static uint FindActorNameBase(byte[]? section, uint actorFormId)
    {
        var offset = FindActorRecord(section, actorFormId)
                     ?? throw new XunitException($"Actor 0x{actorFormId:X8} not emitted.");
        var name = FindSubrecord(section!, offset, "NAME")
                   ?? throw new XunitException("Emitted actor has no NAME subrecord.");
        return BinaryPrimitives.ReadUInt32LittleEndian(name);
    }

    /// <summary>Record byte-offset of the first non-CELL record with the given FormID, or null.</summary>
    private static int? FindActorRecord(byte[]? section, uint formId)
    {
        if (section is null) return null;
        var pos = 0;
        while (pos + 24 <= section.Length)
        {
            var sig = Encoding.ASCII.GetString(section, pos, 4);
            if (sig == "GRUP")
            {
                pos += 24;
                continue;
            }

            var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 4, 4));
            var recFormId = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(pos + 12, 4));
            if (sig != "CELL" && recFormId == formId) return pos;
            pos += 24 + dataSize;
        }

        return null;
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
            if (sig == signature) return section.AsSpan(pos + 6, len).ToArray();
            pos += 6 + len;
        }

        return null;
    }
}