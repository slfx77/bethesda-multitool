using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class PlacedReferenceEnableStateResolverTests
{
    [Fact]
    public void NormalAndInverseParents_ResolveAuthoredInitialState()
    {
        var parent = Placement(0x10, initiallyDisabled: true);
        var normal = Placement(0x11, parent: parent.FormId);
        var inverse = Placement(0x12, parent: parent.FormId, parentFlags: 1);

        var disabled = Resolve([Cell(0x100, parent, normal, inverse)]);

        Assert.Contains(normal.FormId, disabled);
        Assert.DoesNotContain(inverse.FormId, disabled);
    }

    [Fact]
    public void MultiLevelChain_AppliesEveryInverseEdge()
    {
        var root = Placement(0x20, initiallyDisabled: true);
        var middle = Placement(0x21, parent: root.FormId, parentFlags: 1);
        var normalChild = Placement(0x22, parent: middle.FormId);
        var inverseChild = Placement(0x23, parent: middle.FormId, parentFlags: 1);

        var disabled = Resolve([Cell(0x200, root, middle, normalChild, inverseChild)]);

        Assert.DoesNotContain(middle.FormId, disabled);
        Assert.DoesNotContain(normalChild.FormId, disabled);
        Assert.Contains(inverseChild.FormId, disabled);
    }

    [Fact]
    public void AcyclicChainLongerThanTheFormerDepthLimit_ResolvesTheRootState()
    {
        const int edgeCount = 20;
        var placements = new List<PlacedReference>
        {
            Placement(0x100, initiallyDisabled: true)
        };
        for (var edge = 1; edge <= edgeCount; edge++)
        {
            placements.Add(Placement(
                0x100u + (uint)edge,
                parent: placements[^1].FormId));
        }

        var disabled = Resolve([Cell(0x250, [.. placements])]);

        Assert.Contains(placements[^1].FormId, disabled);
    }

    [Fact]
    public void CrossCellParent_IsResolvedFromTheCompletePlacementIndex()
    {
        var parent = Placement(0x30, initiallyDisabled: true);
        var child = Placement(0x31, parent: parent.FormId);

        var disabled = Resolve([Cell(0x300, child), Cell(0x301, parent)]);

        Assert.Contains(child.FormId, disabled);
    }

    [Fact]
    public void MissingParent_PreservesTheEffectiveOwnAuthoredState()
    {
        var enabledChild = Placement(0x40, parent: 0xDEADBEEF, parentFlags: 1);
        var alreadyDisabledChild = Placement(
            0x41,
            initiallyDisabled: true,
            parent: 0xDEADBEEF,
            parentFlags: 1);

        var disabled = Resolve([Cell(0x400, enabledChild, alreadyDisabledChild)]);

        Assert.False(enabledChild.IsInitiallyDisabled || disabled.Contains(enabledChild.FormId));
        // Own Initially Disabled is deliberately handled by the caller, not duplicated in this set.
        Assert.True(alreadyDisabledChild.IsInitiallyDisabled ||
                    disabled.Contains(alreadyDisabledChild.FormId));
    }

    [Fact]
    public void SelfCycle_IsConservativelyDisabledRegardlessOfInverseFlag()
    {
        var normal = Placement(0x50, parent: 0x50);
        var inverse = Placement(0x51, parent: 0x51, parentFlags: 1);

        var disabled = Resolve([Cell(0x500, normal, inverse)]);

        Assert.Contains(normal.FormId, disabled);
        Assert.Contains(inverse.FormId, disabled);
    }

    [Fact]
    public void MultiNodeCycle_IsConservativelyDisabledWithoutParityDependence()
    {
        var first = Placement(0x60, parent: 0x61, parentFlags: 1);
        var second = Placement(0x61, parent: 0x62);
        var third = Placement(0x62, parent: 0x60, parentFlags: 1);

        var disabled = Resolve([Cell(0x600, first, second, third)]);

        Assert.Contains(first.FormId, disabled);
        Assert.Contains(second.FormId, disabled);
        Assert.Contains(third.FormId, disabled);
    }

    private static HashSet<uint> Resolve(IReadOnlyList<CellRecord> cells) =>
        PlacedReferenceEnableStateResolver.ResolveXespDisabledRefs(cells);

    private static CellRecord Cell(uint formId, params PlacedReference[] placements) => new()
    {
        FormId = formId,
        PlacedObjects = [.. placements]
    };

    private static PlacedReference Placement(
        uint formId,
        bool initiallyDisabled = false,
        uint? parent = null,
        byte? parentFlags = null) => new()
    {
        FormId = formId,
        BaseFormId = 0x01000000 + formId,
        RecordType = "REFR",
        IsInitiallyDisabled = initiallyDisabled,
        EnableParentFormId = parent,
        EnableParentFlags = parentFlags
    };
}
