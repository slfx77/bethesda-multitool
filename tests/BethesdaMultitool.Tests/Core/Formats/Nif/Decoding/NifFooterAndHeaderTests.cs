using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     The file-level self-checks around the block decoder: the header re-read (and its cross-check against
///     NifParser), the raw string table, the layout (header end, block tiling) and the footer NifParser never reads.
/// </summary>
public class NifFooterAndHeaderTests
{
    private static NifTestFileBuilder TwoNodes(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, -1, [1]));
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, -1, []));
        return builder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateLayout_ReturnsTheFooterAfterTheLastBlock(bool bigEndian)
    {
        var bytes = TwoNodes(bigEndian).WithRoot(0).WithRoot(1).Build();
        var decoder = NifDecodingTestSupport.Open(bytes);

        var footer = decoder.ValidateLayout();

        Assert.Equal(new[] { 0, 1 }, footer.Roots.ToArray());
        Assert.Equal(12, footer.Length);
        Assert.Equal(bytes.Length - 12, footer.Offset);
        Assert.Equal(decoder.FooterOffset, footer.Offset);
        Assert.Equal(decoder.Header.HeaderEnd + decoder.Header.BlockSizes.Sum(s => (int)s), footer.Offset);
    }

    /// <summary>
    ///     Byte-order control: root 1 is stored in the body order. Read the other way it would be 0x01000000, which is
    ///     out of range, so this passes only when the footer is read in the file's order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFooter_ReadsRootsInTheBodyByteOrder(bool bigEndian)
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(bigEndian).WithRoot(1).Build());

        Assert.Equal(new[] { 1 }, decoder.ReadFooter().Roots.ToArray());
    }

    [Fact]
    public void ReadFooter_TrailingByteAfterTheFooter_Throws()
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(false).WithTrailingBytes(0).Build());

        var error = Assert.Throws<InvalidDataException>(() => decoder.ValidateLayout());
        Assert.Contains("trailing", error.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void ReadFooter_RootOutsideTheBlocks_Throws(int root)
    {
        var decoder = NifDecodingTestSupport.Open(TwoNodes(false).WithFooterRootsOverride(root).Build());

        Assert.Throws<InvalidDataException>(() => decoder.ReadFooter());
    }

    [Fact]
    public void ReadFooter_RootCountLargerThanTheFile_Throws()
    {
        byte[] footer = [0xFF, 0xFF, 0xFF, 0x0F, 0, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => NifFooterReader.Read(footer, false, 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Header_ReReadMatchesTheParser(bool bigEndian)
    {
        var builder = TwoNodes(bigEndian);
        builder.AddString("first");
        builder.AddString("second");
        var bytes = builder.Build();
        var info = NifParser.Parse(bytes)!;
        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes);

        var header = decoder.Header;
        Assert.Equal(info.Blocks[0].DataOffset, header.HeaderEnd);
        Assert.Equal(bigEndian, header.IsBigEndian);
        Assert.Equal(34u, header.BsVersion);
        Assert.Equal(11u, header.UserVersion);
        Assert.Equal(new[] { "NiNode" }, header.BlockTypeNames.ToArray());
        Assert.Equal(2, header.Strings.Count);
        Assert.Equal("second", header.Strings.GetText(1));
        Assert.Equal(info.Blocks.Select(b => (uint)b.Size).ToArray(), header.BlockSizes.ToArray());
    }

    /// <summary>Control for the cross-check: a parser result that disagrees with the bytes is refused.</summary>
    [Fact]
    public void Header_ParserResultThatDisagreesWithTheBytes_IsRefused()
    {
        var bytes = TwoNodes(false).Build();
        var info = NifParser.Parse(bytes)!;
        info.Blocks[1].Size += 4;

        Assert.Throws<InvalidDataException>(() => new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes));
    }

    /// <summary>
    ///     String-table bytes at or above 0x80 survive as raw bytes and Latin-1 text. NifParser's ASCII decoding turns
    ///     each of them into '?', which is the loss this re-read exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringTable_HighBytes_AreKeptRawAndReadAsLatin1(bool bigEndian)
    {
        byte[] raw = [0x43, 0x61, 0x66, 0xE9, 0x20, 0x80, 0xFF];
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var name = builder.AddRawString(raw);
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, name, []));
        var bytes = builder.Build();
        var info = NifParser.Parse(bytes)!;

        var value = NifDecodingTestSupport.Open(bytes).Decode(0, NifDecodeMode.Strict).Root.Get<NifStringValue>("Name");

        Assert.Equal(raw, value.RawBytes.ToArray());
        Assert.Equal("Caf\u00E9 \u0080\u00FF", value.Text);
        Assert.True(value.HasNonAsciiBytes);
        Assert.Equal("Caf? ??", info.Strings[0]);
        Assert.NotEqual(info.Strings[0], value.Text);
    }

    [Fact]
    public void StringIndexOutsideTheTable_FailsStrict_AndIsReportedTolerant()
    {
        var builder = new NifTestFileBuilder(false, 34);
        builder.AddString("only");
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, 5, []));
        var decoder = NifDecodingTestSupport.Open(builder.Build());

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.StringIndex, strict.Failure.Kind);
        Assert.Equal("Name.Index", strict.Failure.FieldPath); // the string struct's NiFixedString field

        var tolerant = decoder.Decode(0, NifDecodeMode.Tolerant);
        Assert.Equal(NifDecodeFailureKind.StringIndex, Assert.Single(tolerant.Problems).Kind);
        Assert.False(tolerant.Root.Get<NifStringValue>("Name").IsResolved);
    }
}
