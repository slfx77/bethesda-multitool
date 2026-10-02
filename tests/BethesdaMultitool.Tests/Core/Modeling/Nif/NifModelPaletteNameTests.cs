using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 8, NiDefaultAVObjectPalette: a palette name becomes the display name of a node whose own name is null (the
///     Xbox case), the node's own name stays authoritative otherwise, and the palette is Typed only when it supplied a
///     display name.
/// </summary>
public class NifModelPaletteNameTests
{
    /// <summary>
    ///     0 NiNode "Root" (controller 1) [3, 4]; 1 NiControllerManager (object palette 2); 2 NiDefaultAVObjectPalette
    ///     naming <paramref name="objects" />; 3 NiNode named <paramref name="thirdName" /> (null when absent); 4 NiNode
    ///     "Own".
    /// </summary>
    private static byte[] Fixture((string Name, int Target)[] objects, string? thirdName = null,
        bool bigEndian = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var rootName = builder.AddString("Root");
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, rootName, [3, 4], _ => { },
            controller: 1));
        builder.AddBlock("NiControllerManager", w => NifTestBlockLayouts.ControllerManager(w, 0, 2));
        builder.AddBlock("NiDefaultAVObjectPalette", w => NifTestBlockLayouts.DefaultAvObjectPalette(w, 0, objects));
        AddNode(builder, thirdName is null ? -1 : builder.AddString(thirdName), []);
        AddNode(builder, builder.AddString("Own"), []);
        return builder.Build();
    }

    /// <summary>
    ///     The unnamed node takes "Bip01 Head" from the palette and the palette is Typed; the named node keeps "Own" and
    ///     the differing palette name is native only. Control: when every node has its own name, the palette supplies no
    ///     display name and, since no animation binding used it either, stays NativeOnly with its plan 2.1 row (cut-1b
    ///     slice 10; the 'later-cut(1b)' reason is retired).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaletteName_NamesAnUnnamedNode_AndTheNodeNameStaysAuthoritative(bool bigEndian)
    {
        (string, int)[] objects = [("Bip01 Head", 3), ("Palette Other", 4)];
        var result = Read(Fixture(objects, bigEndian: bigEndian));
        var document = result.Document;

        Assert.Equal(["Root", "Bip01 Head", "Own"], document.Nodes.Select(n => n.Name));
        var unnamed = BlockPayload(document, 3)["node"]!;
        Assert.Equal(NifModelPaletteNames.PaletteNameSource, (string)unnamed["nameSource"]!);
        Assert.True((bool)unnamed["palette"]!["usedAsDisplayName"]!);
        var named = BlockPayload(document, 4)["node"]!;
        Assert.Equal("stringTable", (string)named["nameSource"]!);
        Assert.Equal("Palette Other", (string)named["palette"]!["name"]!["text"]!);
        Assert.False((bool)named["palette"]!["usedAsDisplayName"]!);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:2").Kind);
        SceneValidation.ValidateStructure(document);

        var allNamed = Read(Fixture(objects, "Named")).Coverage.GetClassification("block:2");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, allNamed.Kind);
        Assert.Equal(NifModelAnimationCoverage.PaletteNoBindingReason, allNamed.Reason);
    }

    /// <summary>
    ///     Two entries giving one block different names are ambiguous: neither is used and a diagnostic names both.
    ///     Control: the same name twice is not a conflict and names the node.
    /// </summary>
    [Fact]
    public void ConflictingPaletteNames_AreNotUsed()
    {
        var conflicting = Read(Fixture([("A", 3), ("B", 3)])).Document;
        var agreeing = Read(Fixture([("A", 3), ("A", 3)])).Document;

        Assert.Equal("", conflicting.Nodes[1].Name);
        Assert.Contains(conflicting.Diagnostics, d => d.Code == NifModelPaletteNames.ConflictDiagnostic);
        Assert.Equal("A", agreeing.Nodes[1].Name);
        Assert.DoesNotContain(agreeing.Diagnostics, d => d.Code == NifModelPaletteNames.ConflictDiagnostic);
    }
}
