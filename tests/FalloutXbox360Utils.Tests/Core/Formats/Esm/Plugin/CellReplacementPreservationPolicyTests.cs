using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
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

    private static ParsedMainRecord MakeMasterRef(
        uint formId,
        uint baseFormId,
        bool persistent,
        float x,
        float y,
        float z)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                FormId = formId,
                Flags = persistent ? PersistentFlag : 0,
                Version = 0x000F
            },
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakePositionSubrecord(x, y, z)
            ]
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
