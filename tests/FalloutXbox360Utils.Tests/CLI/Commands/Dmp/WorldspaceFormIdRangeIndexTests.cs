using FalloutXbox360Utils.CLI.Commands.Dmp;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

public class WorldspaceFormIdRangeIndexTests
{
    [Fact]
    public void ObserveCell_IgnoresVirtualCellFormIds()
    {
        var index = new WorldspaceFormIdRangeIndex();

        index.ObserveCell(new CellRecord { FormId = 0xFE000001, WorldspaceFormId = 0x100 }, new Dictionary<uint, uint>());

        Assert.Empty(index.Ranges);
    }

    [Fact]
    public void ResolveUniqueOwner_CandidateConstrainedUniqueMatch_Succeeds()
    {
        var index = new WorldspaceFormIdRangeIndex();
        index.AddObservedCell(0x100, 0x1000);
        index.AddObservedCell(0x100, 0x1100);
        index.AddObservedCell(0x200, 0x3000);
        index.AddObservedCell(0x200, 0x3100);
        var cell = new CellRecord
        {
            FormId = 0x1080,
            CandidateWorldspaceFormIds = [0x100, 0x200]
        };

        var owner = index.ResolveUniqueOwner(cell);

        Assert.Equal(0x100u, owner);
    }

    [Fact]
    public void ResolveUniqueOwner_OverlappingRangesStayAmbiguous()
    {
        var index = new WorldspaceFormIdRangeIndex();
        index.AddObservedCell(0x100, 0x1000);
        index.AddObservedCell(0x100, 0x3000);
        index.AddObservedCell(0x200, 0x2000);
        index.AddObservedCell(0x200, 0x4000);
        var cell = new CellRecord { FormId = 0x2500 };

        var owner = index.ResolveUniqueOwner(cell);

        Assert.Null(owner);
    }

    [Fact]
    public void ResolveUniqueOwner_OverlappingRangesCanBeConstrainedByCandidateSet()
    {
        var index = new WorldspaceFormIdRangeIndex();
        index.AddObservedCell(0x100, 0x1000);
        index.AddObservedCell(0x100, 0x3000);
        index.AddObservedCell(0x200, 0x2000);
        index.AddObservedCell(0x200, 0x4000);
        var cell = new CellRecord
        {
            FormId = 0x2500,
            CandidateWorldspaceFormIds = [0x100]
        };

        var owner = index.ResolveUniqueOwner(cell);

        Assert.Equal(0x100u, owner);
    }

    [Fact]
    public void ObserveCell_UsesRuntimeOwnerWhenCellHasNoWorldspaceFormId()
    {
        var index = new WorldspaceFormIdRangeIndex();

        index.ObserveCell(
            new CellRecord { FormId = 0x1200 },
            new Dictionary<uint, uint> { [0x1200] = 0x300 });

        Assert.Equal((0x1200u, 0x1200u), index.Ranges[0x300]);
    }
}
