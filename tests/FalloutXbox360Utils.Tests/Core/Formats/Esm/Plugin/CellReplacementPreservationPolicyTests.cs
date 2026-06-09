using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
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
        var masterRef = MakeMasterRef(0x200, 0x101, false, 10f, 20f, 30f);

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
        var masterRef = MakeMasterRef(0x200, 0x100, false, 10f, 20f, 30f);

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
        var masterRef = MakeMasterRef(0x200, 0x100, false, 500f, 20f, 30f);

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

        var sourceMasterRef = MakeMasterRef(0x300, sourceBase, false, 1f, 2f, 3f);
        var allocatedMasterRef = MakeMasterRef(0x301, allocatedBase, false, 1f, 2f, 3f);

        Assert.False(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(sourceMasterRef, placementsByBase));
        Assert.False(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(allocatedMasterRef, placementsByBase));
    }

    [Fact]
    public void PreserveFilter_PersistentMasterRef_IsAlwaysPreserved()
    {
        var placementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
            [MakePlacement(0x100, 10f, 20f, 30f)],
            new Dictionary<uint, uint>());
        var masterRef = MakeMasterRef(0x200, 0x100, true, 10f, 20f, 30f);

        Assert.True(CellReplacementPreservationPolicy.ShouldPreserveMasterRef(masterRef, placementsByBase));
    }

    [Fact]
    public void PreserveAllMissing_CopiesVanillaRefsToTheirOriginalChildBuckets()
    {
        const uint cellFormId = 0x00103DF9;
        var persistent = MakeMasterRef(0x200, 0x100, true, 10f, 20f, 30f);
        var temporary = MakeMasterRef(0x201, 0x101, false, 10f, 20f, 30f);
        var vwd = MakeMasterRef(0x202, 0x102, false, 10f, 20f, 30f);
        var seenInDmp = MakeMasterRef(0x203, 0x103, false, 10f, 20f, 30f);
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
        var ordinary = MakeMasterRef(0x200, 0x100, false, 10f, 20f, 30f);
        var covered = MakeMasterRef(0x201, 0x101, true, 10f, 20f, 30f);
        var actor = MakeMasterRef(0x202, 0x102, false, 10f, 20f, 30f, "ACHR");
        var persistent = MakeMasterRef(0x203, 0x103, true, 10f, 20f, 30f);
        var scriptedRef = MakeMasterRef(
            0x204, 0x104, false, 10f, 20f, 30f,
            extraSubrecords: [MakeFormIdSubrecord("SCRI", 0x500)]);
        var scriptedBase = MakeMasterRef(0x205, 0x105, false, 10f, 20f, 30f);
        var structural = MakeMasterRef(
            0x206, 0x106, false, 10f, 20f, 30f,
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
            0x207, 0x107, false, 10f, 20f, 30f,
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

    // ====================================================================================
    // Type-set-based preservation tests for the Doc Mitchell sparse-cell-replacement bug.
    // When the DMP capture for a LoadedReplacement cell only contains certain base types
    // (e.g. DOOR / ACHR / FURN — typical of a small interior cell where the player walked
    // through), master refs whose base type IS NOT in the DMP capture should be preserved
    // rather than delete-marked. Without this, master STATs disappear and the cell looks
    // empty.
    // ====================================================================================

    [Fact]
    public void ShouldPreserveInLoadedReplacement_Preserves_Master_Stat_When_Dmp_Has_No_Stat()
    {
        const uint masterStatRefFormId = 0x300;
        const uint masterStatBaseFormId = 0x150;
        var statRef = MakeMasterRef(masterStatRefFormId, masterStatBaseFormId, false, 0f, 0f, 0f);
        var statBase = MakeBaseRecord("STAT", masterStatBaseFormId);
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterStatBaseFormId] = statBase
        };

        // DMP captured only DOOR + ACHR + FURN — no STAT. Master STAT must be preserved.
        var dmpCapturedBaseTypes = new HashSet<string>(StringComparer.Ordinal) { "DOOR", "ACHR", "FURN" };

        var shouldPreserve = CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
            statRef, pcRecords, dmpCapturedBaseTypes);

        Assert.True(shouldPreserve);
    }

    [Fact]
    public void ShouldPreserveInLoadedReplacement_Allows_Master_Stat_Deletion_When_Dmp_Has_Stat()
    {
        // When the DMP captured STAT placements, master's STATs are authoritatively under
        // the DMP's control. ShouldPreserveInLoadedReplacement returns false for ordinary
        // refs (the binary policy's intent stands when DMP did capture the type).
        const uint masterStatRefFormId = 0x300;
        const uint masterStatBaseFormId = 0x150;
        var statRef = MakeMasterRef(masterStatRefFormId, masterStatBaseFormId, false, 0f, 0f, 0f);
        var statBase = MakeBaseRecord("STAT", masterStatBaseFormId);
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterStatBaseFormId] = statBase
        };

        var dmpCapturedBaseTypes = new HashSet<string>(StringComparer.Ordinal) { "DOOR", "ACHR", "STAT" };

        var shouldPreserve = CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
            statRef, pcRecords, dmpCapturedBaseTypes);

        Assert.False(shouldPreserve);
    }

    [Fact]
    public void ShouldPreserveInLoadedReplacement_Script_Bearing_Wins_Even_When_Type_Set_Match()
    {
        // The existing script-critical rule still takes precedence. Even if the DMP captured
        // a STAT and our master ref's base is also STAT, a script-bearing ref must survive.
        const uint masterStatRefFormId = 0x300;
        const uint masterStatBaseFormId = 0x150;
        var scriptedStatRef = MakeMasterRef(
            masterStatRefFormId, masterStatBaseFormId, false, 0f, 0f, 0f,
            extraSubrecords: [MakeFormIdSubrecord("SCRI", 0x500)]);
        var statBase = MakeBaseRecord("STAT", masterStatBaseFormId);
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterStatBaseFormId] = statBase
        };

        var dmpCapturedBaseTypes = new HashSet<string>(StringComparer.Ordinal) { "STAT" };

        var shouldPreserve = CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
            scriptedStatRef, pcRecords, dmpCapturedBaseTypes);

        Assert.True(shouldPreserve); // SCRI rule wins
    }

    [Fact]
    public void ShouldPreserveInLoadedReplacement_Falls_Back_To_Binary_When_TypeSet_Is_Null()
    {
        // When no dmpCapturedBaseTypes is supplied (existing callers / legacy tests), the
        // method behavior matches the pre-fix binary policy: ordinary STAT refs are not
        // preserved.
        const uint masterStatRefFormId = 0x300;
        const uint masterStatBaseFormId = 0x150;
        var statRef = MakeMasterRef(masterStatRefFormId, masterStatBaseFormId, false, 0f, 0f, 0f);
        var statBase = MakeBaseRecord("STAT", masterStatBaseFormId);
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterStatBaseFormId] = statBase
        };

        var shouldPreserve = CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
            statRef, pcRecords);

        Assert.False(shouldPreserve);
    }
}