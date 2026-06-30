using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Show;

/// <summary>
///     Guards that the generic <c>show</c> renderer surfaces the full schema DecodedTree (every subrecord)
///     for schema-decoded games. Regression: an Oblivion TREE rendered as a bare summary (model only), hiding
///     ICON/SNAM/CNAM/BNAM, so the CLI could not be used to inspect tree data.
/// </summary>
public class GenericShowRendererTests
{
    [Fact]
    public void AppendDecodedTree_RendersLeavesStructsArraysAndReferences()
    {
        // A miniature of an Oblivion TREE: a string leaf, a struct with children, an array element, and a
        // FormID reference whose Value is null (so the hex fallback must kick in).
        var tree = new List<DecodedNode>
        {
            new() { Label = "Leaf Texture", Signature = "ICON", Value = "TreeWhitePineNeedles.dds" },
            new()
            {
                Label = "Tree Data",
                Signature = "CNAM",
                Children =
                [
                    new DecodedNode { Label = "Leaf Curvature", Value = "2.5" },
                    new DecodedNode { Label = "Shadow Radius", Value = "475" }
                ]
            },
            new() { Label = "Open Sound", Signature = "SNAM", Value = null, FormId = 0x0005E161 },
            new() { Label = "Unknown", Signature = "XXXX", Value = "deadbeef", IsRaw = true }
        };

        var lines = new List<string>();
        GenericShowRenderer.AppendDecodedTree(lines, tree, 0);

        var text = string.Join("\n", lines);

        // Leaf with signature + value.
        Assert.Contains("Leaf Texture", text);
        Assert.Contains("(ICON)", text);
        Assert.Contains("TreeWhitePineNeedles.dds", text);

        // Struct header followed by indented children (depth 1 == two-space indent).
        Assert.Contains("Tree Data", text);
        var curvature = lines.Single(l => l.Contains("Leaf Curvature"));
        Assert.StartsWith("  ", curvature);
        Assert.Contains("2.5", curvature);

        // FormID node with a null Value falls back to the hex form.
        Assert.Contains("0x0005E161", text);

        // Raw nodes are flagged so coverage gaps stay visible.
        var rawLine = lines.Single(l => l.Contains("Unknown"));
        Assert.Contains("raw", rawLine);
    }
}
