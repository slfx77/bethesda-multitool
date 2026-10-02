using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     NiNode through the schema-driven decoder, in both byte orders and every cut-1a BS version, plus the block-level
///     self-checks: the per-block size check, reference range and reference template.
/// </summary>
public class NifBlockDecoderNodeTests
{
    private static readonly float[] Rotation = [0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];

    private static NifTestFileBuilder TwoNodes(bool bigEndian, uint bs, int childOfRoot = 1,
        Action<NifTestBlockWriter>? appendToRoot = null)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        var name = builder.AddString("Scene Root");
        builder.AddBlock("NiNode", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, name);
            NifTestBlockLayouts.AvObject(w, bs, 0x0008000E, (1.5f, -2f, 3.25f), Rotation, 0.5f);
            NifTestBlockLayouts.NodeTail(w, [childOfRoot]);
            appendToRoot?.Invoke(w);
        });
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, bs, -1, []));
        return builder;
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 21u)]
    [InlineData(false, 26u)]
    [InlineData(false, 32u)]
    [InlineData(false, 34u)]
    [InlineData(true, 14u)]
    [InlineData(true, 21u)]
    [InlineData(true, 26u)]
    [InlineData(true, 32u)]
    [InlineData(true, 34u)]
    public void NiNode_DecodesEveryFieldExactly(bool bigEndian, uint bs)
    {
        var block = DecodeStrict(TwoNodes(bigEndian, bs), 0);
        var root = block.Root;

        Assert.True(block.IsComplete);
        Assert.Equal(block.Size, block.ConsumedBytes);
        Assert.Equal("NiNode", block.Type);

        var name = root.Get<NifStringValue>("Name");
        Assert.Equal(0, name.Index);
        Assert.Equal("Scene Root", name.Text);

        AssertInteger(root, "Num Extra Data List", 0);
        Assert.Equal(0, root.Get<NifArrayValue>("Extra Data List").Count);
        Assert.Equal(-1, root.Get<NifRefValue>("Controller").Index);

        // nif.xml declares two "Flags": uint for BS > 26, ushort (since 3.0) for BS <= 26. Exactly one decodes.
        var flags = root.Get<NifIntegerValue>("Flags");
        Assert.Equal(bs > 26 ? 4 : 2, flags.ByteWidth);
        Assert.Equal(bs > 26 ? 0x0008000EL : 0x000EL, flags.Value);
        Assert.False(root.Contains("Flags", 1));

        AssertTriple(root, "Translation", ["x", "y", "z"], 1.5f, -2f, 3.25f);
        var rotation = root.Get<NifStructValue>("Rotation");
        string[] cells = ["m11", "m21", "m31", "m12", "m22", "m32", "m13", "m23", "m33"];
        for (var i = 0; i < cells.Length; i++)
        {
            AssertFloat(rotation, cells[i], Rotation[i]);
        }

        AssertFloat(root, "Scale", 0.5f);
        AssertInteger(root, "Num Properties", 0);
        Assert.Equal(-1, root.Get<NifRefValue>("Collision Object").Index);
        Assert.Equal([1], RefIndices(root, "Children"));
        var child = root.Get<NifArrayValue>("Children").Items[0];
        Assert.Equal("NiAVObject", ((NifRefValue)child).Template);
        AssertInteger(root, "Num Effects", 0);
    }

    [Fact]
    public void NiNode_TopLevelSpansTileTheBlockExactly()
    {
        var block = DecodeStrict(TwoNodes(false, 34), 0);

        var topLevel = block.Spans.Where(s => !s.Path.Contains('.') && !s.Path.Contains('[')).ToList();
        Assert.Equal(block.Root.Fields.Count, topLevel.Count);
        var expected = block.Offset;
        foreach (var span in topLevel)
        {
            Assert.Equal(expected, span.Offset);
            expected += span.Length;
        }

        Assert.Equal(block.Offset + block.Size, expected);
        Assert.Contains(block.Spans, s => s.Path == "Translation.y" && s.Length == 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiNode_OneAppendedByte_FailsTheSizeCheck(bool bigEndian)
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(bigEndian, 34, appendToRoot: w => w.U8(0)).Build());

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Size, strict.Failure.Kind);
        Assert.Equal(0, strict.Failure.BlockIndex);
        Assert.Equal("NiNode", strict.Failure.BlockType);
        Assert.Contains("NIF block 0 (NiNode)", strict.Message);

        var tolerant = decoder.Decode(0, NifDecodeMode.Tolerant);
        Assert.NotNull(tolerant.Failure);
        Assert.Equal(NifDecodeFailureKind.Size, tolerant.Failure.Kind);
        Assert.Equal(tolerant.Size - 1, tolerant.ConsumedBytes);
        Assert.Equal(tolerant.Offset + tolerant.Size - 1, tolerant.Failure.Offset);
        Assert.Equal([1], RefIndices(tolerant.Root, "Children"));

        // Control: the same fixture without the extra byte decodes completely.
        Assert.True(DecodeStrict(TwoNodes(bigEndian, 34), 0).IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiNode_ChildOutOfRange_FailsStrict_AndIsReportedTolerant(bool bigEndian)
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(bigEndian, 34, 7).Build());

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Reference, strict.Failure.Kind);
        Assert.Equal("Children[0]", strict.Failure.FieldPath);

        var tolerant = decoder.Decode(0, NifDecodeMode.Tolerant);
        Assert.Null(tolerant.Failure);
        Assert.Equal(tolerant.Size, tolerant.ConsumedBytes);
        var problem = Assert.Single(tolerant.Problems);
        Assert.Equal(NifDecodeFailureKind.Reference, problem.Kind);
        Assert.Equal("Children[0]", problem.FieldPath);
        Assert.Equal([7], RefIndices(tolerant.Root, "Children"));
        Assert.False(tolerant.IsComplete);
    }

    [Fact]
    public void NiNode_ChildThatIsNotAnAvObject_FailsTheTemplateCheck()
    {
        var builder = new NifTestFileBuilder(false, 34);
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, -1, [1]));
        builder.AddBlock("NiAlphaProperty", w =>
        {
            NifTestBlockLayouts.ObjectNet(w, -1);
            w.U16(0x00ED).U8(128);
        });
        var decoder = NifDecodingTestSupport.Open(builder.Build());

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Reference, strict.Failure.Kind);
        Assert.Contains("NiAlphaProperty", strict.Failure.Reason);
        Assert.Contains("NiAVObject", strict.Failure.Reason);

        // Control: the property block itself is well formed.
        Assert.True(decoder.Decode(1, NifDecodeMode.Strict).IsComplete);
    }

    [Fact]
    public void Decode_BlockIndexOutsideTheTable_Throws()
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(false, 34).Build());

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Decode(2, NifDecodeMode.Tolerant));
    }
}
