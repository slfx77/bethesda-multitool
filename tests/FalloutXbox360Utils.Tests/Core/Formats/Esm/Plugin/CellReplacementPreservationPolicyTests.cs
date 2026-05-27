using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellReplacementPreservationPolicyTests
{
    private const uint PersistentFlag = 0x00000400;

    [Fact]
    public void PreserveFilter_UnmatchedMasterRef_IsPreserved()
    {
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(0x100, 10f, 20f, 30f)],
            new Dictionary<uint, uint>());
        var masterRef = MakeMasterRef(0x200, 0x101, persistent: false, 10f, 20f, 30f);

        Assert.True(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(masterRef, placementsByBase));

        var deleted = DeletedRefSynthesizer.Synthesize(
            [masterRef],
            new HashSet<uint>(),
            CellReplacementPreservationPolicy.CreatePreserveFilter(placementsByBase));
        Assert.Empty(deleted.Persistent);
        Assert.Empty(deleted.Temporary);
    }

    [Fact]
    public void PreserveFilter_SameBaseSamePosition_IsDeletionEligible()
    {
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(0x100, 10f, 20f, 30f)],
            new Dictionary<uint, uint>());
        var masterRef = MakeMasterRef(0x200, 0x100, persistent: false, 10f, 20f, 30f);

        Assert.False(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(masterRef, placementsByBase));

        var deleted = DeletedRefSynthesizer.Synthesize(
            [masterRef],
            new HashSet<uint>(),
            CellReplacementPreservationPolicy.CreatePreserveFilter(placementsByBase));
        Assert.Empty(deleted.Persistent);
        Assert.Single(deleted.Temporary);
    }

    [Fact]
    public void PreserveFilter_SameBaseDifferentPosition_IsPreserved()
    {
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(0x100, 10f, 20f, 30f)],
            new Dictionary<uint, uint>());
        var masterRef = MakeMasterRef(0x200, 0x100, persistent: false, 500f, 20f, 30f);

        Assert.True(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(masterRef, placementsByBase));
    }

    [Fact]
    public void BuildPlacementsByBase_IndexesOriginalAndAllocatedBaseIds()
    {
        const uint sourceBase = 0x0100108F;
        const uint allocatedBase = 0xFF000802;
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(sourceBase, 1f, 2f, 3f)],
            new Dictionary<uint, uint> { [sourceBase] = allocatedBase });

        var sourceMasterRef = MakeMasterRef(0x300, sourceBase, persistent: false, 1f, 2f, 3f);
        var allocatedMasterRef = MakeMasterRef(0x301, allocatedBase, persistent: false, 1f, 2f, 3f);

        Assert.False(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(sourceMasterRef, placementsByBase));
        Assert.False(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(allocatedMasterRef, placementsByBase));
    }

    [Fact]
    public void PreserveFilter_PersistentMasterRef_IsAlwaysPreserved()
    {
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(0x100, 10f, 20f, 30f)],
            new Dictionary<uint, uint>());
        var masterRef = MakeMasterRef(0x200, 0x100, persistent: true, 10f, 20f, 30f);

        Assert.True(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(masterRef, placementsByBase));
    }

    [Fact]
    public void PreserveAllMissing_CopiesVanillaRefsToTheirOriginalChildBuckets()
    {
        const uint cellFormId = 0x00103DF9;
        var persistent = MakeMasterRef(0x200, 0x100, persistent: true, 10f, 20f, 30f);
        var temporary = MakeMasterRef(0x201, 0x101, persistent: false, 10f, 20f, 30f);
        var vwd = MakeMasterRef(0x202, 0x102, persistent: false, 10f, 20f, 30f);
        var seenInDmp = MakeMasterRef(0x203, 0x103, persistent: false, 10f, 20f, 30f);
        var locations = new Dictionary<uint, MasterChildLocation>
        {
            [0x200] = new(cellFormId, 8, "REFR"),
            [0x201] = new(cellFormId, 9, "REFR"),
            [0x202] = new(cellFormId, 10, "REFR"),
            [0x203] = new(cellFormId, 9, "REFR")
        };
        var persistentBytes = new List<byte[]>();
        var vwdBytes = new List<byte[]>();
        var temporaryBytes = new List<byte[]>();
        var stats = new ConversionPipelineStats();

        var preserved = CellStructuralReferencePreserver.PreserveAllMissing(
            [persistent, temporary, vwd, seenInDmp],
            new HashSet<uint> { 0x203 },
            locations,
            persistentBytes,
            vwdBytes,
            temporaryBytes,
            stats);

        Assert.Equal(3, preserved);
        Assert.Single(persistentBytes);
        Assert.Single(vwdBytes);
        Assert.Single(temporaryBytes);
        Assert.Equal(3, stats.EmittedByType["REFR"]);
    }

    [Fact]
    public void PreserveLoadedReplacementMissing_RetainsOnlyScriptCriticalRefs()
    {
        const uint cellFormId = 0x00103DF9;
        var ordinary = MakeMasterRef(0x200, 0x100, persistent: false, 10f, 20f, 30f);
        var covered = MakeMasterRef(0x201, 0x101, persistent: true, 10f, 20f, 30f);
        var actor = MakeMasterRef(0x202, 0x102, persistent: false, 10f, 20f, 30f, "ACHR");
        var persistent = MakeMasterRef(0x203, 0x103, persistent: true, 10f, 20f, 30f);
        var scriptedRef = MakeMasterRef(
            0x204, 0x104, persistent: false, 10f, 20f, 30f,
            extraSubrecords: [MakeFormIdSubrecord("SCRI", 0x500)]);
        var scriptedBase = MakeMasterRef(0x205, 0x105, persistent: false, 10f, 20f, 30f);
        var structural = MakeMasterRef(
            0x206, 0x106, persistent: false, 10f, 20f, 30f,
            extraSubrecords: [new ParsedSubrecord { Signature = "XPRM", Data = [0x01] }]);
        var locations = new Dictionary<uint, MasterChildLocation>
        {
            [0x200] = new(cellFormId, 9, "REFR"),
            [0x201] = new(cellFormId, 8, "REFR"),
            [0x202] = new(cellFormId, 8, "ACHR"),
            [0x203] = new(cellFormId, 8, "REFR"),
            [0x204] = new(cellFormId, 9, "REFR"),
            [0x205] = new(cellFormId, 9, "REFR"),
            [0x206] = new(cellFormId, 9, "REFR")
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [0x105] = MakeBaseRecord("ACTI", 0x105, MakeFormIdSubrecord("SCRI", 0x501))
        };
        var persistentBytes = new List<byte[]>();
        var vwdBytes = new List<byte[]>();
        var temporaryBytes = new List<byte[]>();
        var stats = new ConversionPipelineStats();

        var preserved = CellStructuralReferencePreserver.PreserveLoadedReplacementMissing(
            [ordinary, covered, actor, persistent, scriptedRef, scriptedBase, structural],
            new HashSet<uint> { 0x201 },
            locations,
            pcRecords,
            persistentBytes,
            vwdBytes,
            temporaryBytes,
            stats);

        Assert.Equal(4, preserved);
        Assert.Equal(2, persistentBytes.Count);
        Assert.Empty(vwdBytes);
        Assert.Equal(2, temporaryBytes.Count);
        Assert.Equal(3, stats.EmittedByType["REFR"]);
        Assert.Equal(1, stats.EmittedByType["ACHR"]);
    }

    [Fact]
    public void PreserveLoadedReplacementMissing_DmpStructuralRefsSuppressMasterStructuralRefs()
    {
        const uint cellFormId = 0x00103DF9;
        var scriptedStructural = MakeMasterRef(
            0x207, 0x107, persistent: false, 10f, 20f, 30f,
            extraSubrecords:
            [
                MakeFormIdSubrecord("SCRI", 0x500),
                new ParsedSubrecord { Signature = "XOCP", Data = [0x01, 0x02, 0x03, 0x04] }
            ]);
        var locations = new Dictionary<uint, MasterChildLocation>
        {
            [0x207] = new(cellFormId, 9, "REFR")
        };

        var withoutDmpStructural = PreserveSingleLoadedReplacement(scriptedStructural, locations, false);
        Assert.Equal(1, withoutDmpStructural.Preserved);
        Assert.Single(withoutDmpStructural.TemporaryBytes);
        Assert.Equal(1, withoutDmpStructural.Stats.EmittedByType["REFR"]);

        var withDmpStructural = PreserveSingleLoadedReplacement(scriptedStructural, locations, true);
        Assert.Equal(0, withDmpStructural.Preserved);
        Assert.Empty(withDmpStructural.TemporaryBytes);
    }

    private static PlacedReference MakePlacement(uint baseFormId, float x, float y, float z)
    {
        return new PlacedReference
        {
            FormId = 0x500,
            BaseFormId = baseFormId,
            X = x,
            Y = y,
            Z = z
        };
    }

    private static (
        int Preserved,
        List<byte[]> TemporaryBytes,
        ConversionPipelineStats Stats) PreserveSingleLoadedReplacement(
            ParsedMainRecord masterRef,
            IReadOnlyDictionary<uint, MasterChildLocation> locations,
            bool hasAuthoritativeDmpStructuralRefs)
    {
        var persistentBytes = new List<byte[]>();
        var vwdBytes = new List<byte[]>();
        var temporaryBytes = new List<byte[]>();
        var stats = new ConversionPipelineStats();

        var preserved = CellStructuralReferencePreserver.PreserveLoadedReplacementMissing(
            [masterRef],
            new HashSet<uint>(),
            locations,
            new Dictionary<uint, ParsedMainRecord>(),
            persistentBytes,
            vwdBytes,
            temporaryBytes,
            stats,
            hasAuthoritativeDmpStructuralRefs);

        return (preserved, temporaryBytes, stats);
    }

    private static ParsedMainRecord MakeMasterRef(
        uint formId,
        uint baseFormId,
        bool persistent,
        float x,
        float y,
        float z,
        string signature = "REFR",
        params ParsedSubrecord[] extraSubrecords)
    {
        var subrecords = new List<ParsedSubrecord>
        {
            MakeFormIdSubrecord("NAME", baseFormId),
            MakePositionSubrecord(x, y, z)
        };
        subrecords.AddRange(extraSubrecords);

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Flags = persistent ? PersistentFlag : 0,
                Version = 0x000F
            },
            Subrecords = subrecords
        };
    }

    private static ParsedMainRecord MakeBaseRecord(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Version = 0x000F
            },
            Subrecords = [.. subrecords]
        };
    }

    private static ParsedSubrecord MakeFormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return new ParsedSubrecord { Signature = signature, Data = data };
    }

    private static ParsedSubrecord MakePositionSubrecord(float x, float y, float z)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8, 4), z);
        return new ParsedSubrecord { Signature = "DATA", Data = data };
    }
}
