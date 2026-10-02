using System.Numerics;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 8, NiSwitchNode: one exclusive group of layer sets per switch, one set per stored child ordinal (null
///     children keep their ordinal as an empty set), default-on at the stored Index counted over the stored Children.
/// </summary>
public class NifModelSwitchLayerTests
{
    /// <summary>0 NiNode "Root" [1]; 1 NiSwitchNode "Switch" [2, null, 3] with the given Index; 2 "First"; 3 "Third".</summary>
    private static byte[] Fixture(uint index, bool bigEndian = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        var name = builder.AddString("Switch");
        builder.AddBlock("NiSwitchNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [2, -1, 3],
            tail => NifTestBlockLayouts.SwitchTail(tail, 3, index)));
        AddNode(builder, builder.AddString("First"), []);
        AddNode(builder, builder.AddString("Third"), []);
        return builder.Build();
    }

    /// <summary>
    ///     Index 2 names "Third" in the stored Children [First, null, Third]. Control: an implementation that indexes the
    ///     list without nulls ([First, Third]) finds no child at 2, so "Third" being on is what discriminates.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildOrdinals_FormOneExclusiveGroup_WithTheStoredIndexOn(bool bigEndian)
    {
        var result = Read(Fixture(2, bigEndian));
        var document = result.Document;

        Assert.Equal(["Root", "Switch", "First", "Third"], document.Nodes.Select(n => n.Name));
        Assert.Equal(["switch:1:0", "switch:1:1", "switch:1:2"], document.LayerSets.Select(s => s.Id));
        Assert.All(document.LayerSets, set =>
        {
            Assert.Equal("switch:1", set.ExclusiveGroup);
            Assert.Equal(NifModelLayerReader.SwitchSourceKind, set.SourceKind);
        });
        Assert.Equal([2], document.LayerSets[0].Members);
        Assert.Empty(document.LayerSets[1].Members);
        Assert.Equal([3], document.LayerSets[2].Members);
        Assert.Equal(["First", "", "Third"], document.LayerSets.Select(s => s.Label));
        Assert.Equal([false, false, true], document.LayerSets.Select(s => s.DefaultOn));
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);

        var facts = BlockPayload(document, 1)["node"]!["switch"]!;
        Assert.Equal(2, (int)facts["defaultOnOrdinal"]!);
        Assert.Equal(3, (int)facts["childSlots"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     An Index naming a null child turns on that ordinal's empty set, so nothing is shown. Control: the list without
    ///     nulls would show "Third" at index 1.
    /// </summary>
    [Fact]
    public void IndexAtANullChild_TurnsOnTheEmptySet()
    {
        var document = Read(Fixture(1)).Document;

        Assert.Equal([false, true, false], document.LayerSets.Select(s => s.DefaultOn));
        Assert.Empty(document.LayerSets[1].Members);
        Assert.DoesNotContain(document.LayerSets, set => set.DefaultOn && set.Members.Contains(3));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>An Index past the children leaves every set off and is reported. Control: Index 2 reports nothing.</summary>
    [Fact]
    public void IndexPastTheChildren_LeavesEverySetOff_AndIsReported()
    {
        var document = Read(Fixture(5)).Document;

        Assert.All(document.LayerSets, set => Assert.False(set.DefaultOn));
        Assert.Contains(document.Diagnostics, d => d.Code == NifModelLayerReader.SwitchIndexDiagnostic);
        Assert.DoesNotContain(Read(Fixture(2)).Document.Diagnostics,
            d => d.Code == NifModelLayerReader.SwitchIndexDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     An instanced switch lists every occurrence of a child in that child's set. Control: Shared rejects two
    ///     default-on sets in one exclusive group, so the reader must keep one group per switch block.
    /// </summary>
    [Fact]
    public void InstancedSwitch_ListsEveryOccurrence_InOneGroup()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1, 2]);
        AddNode(builder, builder.AddString("Left"), [3]);
        AddNode(builder, builder.AddString("Right"), [3]);
        var name = builder.AddString("Switch");
        builder.AddBlock("NiSwitchNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [4],
            tail => NifTestBlockLayouts.SwitchTail(tail, 3, 0)));
        AddNode(builder, builder.AddString("Only"), []);

        var document = Read(builder.Build()).Document;

        Assert.Equal(["Root", "Left", "Switch", "Only", "Right", "Switch", "Only"], document.Nodes.Select(n => n.Name));
        var set = Assert.Single(document.LayerSets);
        Assert.Equal([3, 6], set.Members);
        Assert.True(set.DefaultOn);
        SceneValidation.ValidateStructure(document);

        var twoDefaults = new ModelDocument("test", "two", [new SceneDefinition("s", [0])],
            [new SceneNode("A", Matrix4x4.Identity)], [],
            layerSets:
            [
                new SceneLayerSet("a", "", [0], true, "k", "g"),
                new SceneLayerSet("b", "", [0], true, "k", "g")
            ]);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(twoDefaults));
    }
}
