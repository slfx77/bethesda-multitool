using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 8, the NiAVObject hidden flag (Flags bit 0): the block becomes one default-off layer set holding every
///     occurrence, while visible nodes belong to no set and so stay on.
/// </summary>
public class NifModelHiddenLayerTests
{
    /// <summary>
    ///     0 "Root" [1, 2]; 1 "Visible" [3]; 2 "Other" [3]; 3 "Hidden" (flags 0x0F), so Hidden is placed twice (nodes 2 and
    ///     4). Control: the visible nodes 0, 1 and 3 appear in no set, and no set is default-on.
    /// </summary>
    [Theory]
    [InlineData(false, 34u)]
    [InlineData(true, 21u)]
    public void HiddenFlag_IsADefaultOffSet_OfEveryOccurrence_AndVisibleNodesStayOn(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        AddNode(builder, builder.AddString("Root"), [1, 2]);
        AddNode(builder, builder.AddString("Visible"), [3]);
        AddNode(builder, builder.AddString("Other"), [3]);
        AddNode(builder, builder.AddString("Hidden"), [], flags: 0x0F);

        var document = Read(builder.Build()).Document;

        Assert.Equal(["Root", "Visible", "Hidden", "Other", "Hidden"], document.Nodes.Select(n => n.Name));
        var set = Assert.Single(document.LayerSets);
        Assert.Equal("hidden:3", set.Id);
        Assert.Equal("Hidden", set.Label);
        Assert.Equal([2, 4], set.Members);
        Assert.False(set.DefaultOn);
        Assert.Null(set.ExclusiveGroup);
        Assert.Equal(NifModelLayerReader.HiddenSourceKind, set.SourceKind);
        foreach (var visible in new[] { 0, 1, 3 })
        {
            Assert.DoesNotContain(visible, set.Members);
        }

        var facts = BlockPayload(document, 3)["node"]!;
        Assert.True((bool)facts["hidden"]!);
        Assert.Equal("hidden:3", (string)facts["hiddenLayer"]!["layerSet"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>A file with no hidden flag has no layer set. Control: setting bit 0 on the child adds one (above).</summary>
    [Fact]
    public void NoHiddenFlag_NoLayerSet()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        AddNode(builder, builder.AddString("Child"), []);

        Assert.Empty(Read(builder.Build()).Document.LayerSets);
    }
}
