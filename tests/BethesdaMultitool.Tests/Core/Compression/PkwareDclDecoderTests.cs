using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the PKWARE DCL ("implode") decoder. The first is the reference's OWN documented
///     example — the eight bytes zlib's <c>contrib/blast.c</c> ships in its test main, which decode
///     to "AIAIAIAIAIAIA" — so it is an expectation from outside this repo entirely, and it
///     exercises a literal, a length/distance match and the end-of-stream code. The second is
///     hand-packed here from the format description (bit order and packing spelled out below), so
///     the two vectors cannot fail together for one shared reason. Two more cover the CODED-literal
///     branch, which no retail byte in this repo reaches: a six-letter vector and one that codes all
///     256 literal symbols in order and must come back as the bytes 0..255.
/// </summary>
public class PkwareDclDecoderTests
{
    /// <summary>
    ///     378 bytes coding literals 0x00..0xFF in order, then the end code (see the test that uses
    ///     it). Packed from the upstream <c>blast.c</c> literal code lengths.
    /// </summary>
    private const string AllCodedLiteralsVector =
        "01042009FC811FF0053EC01B78014FE0019718B8033722E00A5C803370028EC001D8033B600B" +
        "6C8035B002248125B000E6C00C987A50C20126A000C620874D48000B6C7C291E4C5C090E3616" +
        "260C0C74E8D0801148A08105E2C0508C2A12BA2850A108400C4C3E328F8B04A21C360B110CC8" +
        "A083062290401854600011E83B4C06DDA6512916082198644327814C531519023C7870200541" +
        "10007A4017E800128038200688022280302004080202003FC007F0023C0037C00570021C003B" +
        "C006B0022C0033C00430020C003D4007D0023400354005500214003940069002240031400410" +
        "0204003E8007E002380036800560026DA00534810650076A4015A80065A00414810290077240" +
        "16C80069200524810410076240148800612004048100E0077C8017F0006EC00538010760076C" +
        "8015B00066C004180103A007748016D00018801A4007D000544005A000C80012A00410010400" +
        "1E800314002C0003400372400648012800128000C001300014000180012000100008F807";

    /// <summary>
    ///     zlib contrib/blast.c's documented example stream. Its own comment states the result is
    ///     "AIAIAIAIAIAIA": literal 'A', literal 'I', then a length-11 match at distance 2.
    /// </summary>
    private static readonly byte[] BlastExample = [0x00, 0x04, 0x82, 0x24, 0x25, 0x8F, 0x80, 0x7F];

    [Fact]
    public void Decompress_ReferenceExample_DecodesToTheDocumentedString()
    {
        var result = PkwareDclDecoder.Decompress(BlastExample);

        Assert.Equal("AIAIAIAIAIAIA"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_ReferenceExample_LengthBoundedOverloadAgrees()
    {
        var result = PkwareDclDecoder.Decompress(BlastExample, 13);

        Assert.Equal("AIAIAIAIAIAIA"u8.ToArray(), result);
    }

    /// <summary>
    ///     Hand-packed literal-only stream. Header: 0x00 = raw (uncoded) literals, 0x04 = 1 KiB
    ///     dictionary. Then, LSB-first within each byte: a 0 flag bit and the eight bits of 'H'
    ///     (0x48) low bit first, a 0 flag bit and the eight bits of 'i' (0x69), then the
    ///     end-of-stream code — a 1 flag bit, the seven 0 bits that decode to length symbol 15
    ///     (base 264) and eight 1 bits of extra, i.e. 264 + 255 = 519. Thirty-four bits pack into
    ///     0x90, 0xA4, 0x05, 0xFC, 0x03. Byte-aligned that end code is 0x01, 0xFF, which is what
    ///     14 of the retail PACKED.DAT blocks end with; the other 120 carry it at the seven other
    ///     bit offsets.
    /// </summary>
    [Fact]
    public void Decompress_HandPackedRawLiterals_ReadsTheBitsLeastSignificantFirst()
    {
        byte[] input = [0x00, 0x04, 0x90, 0xA4, 0x05, 0xFC, 0x03];

        var result = PkwareDclDecoder.Decompress(input, 2, out var consumed);

        Assert.Equal("Hi"u8.ToArray(), result);
        Assert.Equal(7, consumed);
    }

    /// <summary>
    ///     Hand-packed CODED-literal stream — the branch no retail byte reaches, since all 134
    ///     Daggerfall blocks open <c>00 06</c> (raw literals). Header <c>01 04</c> = coded literals,
    ///     1 KiB dictionary; then six literal flag bits each followed by that literal's canonical
    ///     code from the format's fixed 256-symbol literal table, MSB first with every bit inverted;
    ///     then the end code. The codes come from the <c>litlen[]</c> table as published upstream in
    ///     zlib's <c>contrib/blast.c</c>, not from this repo's copy: <c>'P'</c> is 7 bits
    ///     <c>1101101</c>, <c>'K'</c> 9 bits <c>111100110</c>, <c>'W'</c> 8 bits <c>11101100</c>,
    ///     <c>'A'</c> 6 bits <c>011100</c>, <c>'R'</c> 6 bits <c>100011</c>, <c>'E'</c> 5 bits
    ///     <c>00010</c>. If the production table were permuted these ten fixed bytes would decode to
    ///     some other letters, so the test can fail.
    /// </summary>
    [Fact]
    public void Decompress_HandPackedCodedLiterals_UsesTheFixedLiteralHuffmanCode()
    {
        byte[] input = [0x01, 0x04, 0x48, 0x60, 0x42, 0x16, 0x73, 0xDC, 0x80, 0x7F];

        var result = PkwareDclDecoder.Decompress(input, 6, out var consumed);

        Assert.Equal("PKWARE"u8.ToArray(), result);
        Assert.Equal(10, consumed);
    }

    /// <summary>
    ///     The whole coded-literal table in one vector: 256 coded literals in symbol order, then the
    ///     end code, packed from the upstream <c>blast.c</c> <c>litlen[]</c> lengths. It must decode
    ///     to the identity byte range 0..255, which pins every one of the 256 codes — a single
    ///     transposed pair anywhere in the table swaps two output bytes and fails here. Structural
    ///     checks alone cannot do that: the table's expansion has 256 symbols, a maximum length of
    ///     13 and a Kraft sum of exactly 1 whether or not the symbols are in the right order.
    /// </summary>
    [Fact]
    public void Decompress_EveryCodedLiteralSymbol_DecodesToTheIdentityByteRange()
    {
        var input = Convert.FromHexString(AllCodedLiteralsVector);
        byte[] identity = [.. Enumerable.Range(0, 256).Select(value => (byte)value)];

        var result = PkwareDclDecoder.Decompress(input, 256, out var consumed);

        Assert.Equal(378, input.Length);
        Assert.Equal(identity, result);
        Assert.Equal(378, consumed);
    }

    /// <summary>
    ///     The reference example's end code closes the stream on its last byte, so a decode that
    ///     honours the end code consumes all eight — the property that lets the Daggerfall reader
    ///     verify a block's declared compressed size from the payload instead of trusting it.
    /// </summary>
    [Fact]
    public void Decompress_ReferenceExample_ConsumesTheWholeStream()
    {
        PkwareDclDecoder.Decompress(BlastExample, 13, out var consumed);

        Assert.Equal(8, consumed);
    }

    /// <summary>
    ///     A stream with more to say than the declared length must be REJECTED, not trimmed: the
    ///     decoder used to clamp the last match to the limit, which turns a framing error into a
    ///     plausible-looking short block.
    /// </summary>
    [Fact]
    public void Decompress_StreamProducingMoreThanDeclared_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(BlastExample, 5));

        Assert.Contains("more than the declared 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompress_LiteralFlagOutOfRange_Throws()
    {
        byte[] input = [0x02, 0x04, 0x90, 0xA4, 0x01];

        var error = Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(input, 2));

        Assert.Contains("literal flag", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompress_DictionaryExponentOutOfRange_Throws()
    {
        byte[] input = [0x00, 0x07, 0x90, 0xA4, 0x01];

        var error = Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(input, 2));

        Assert.Contains("dictionary exponent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompress_TruncatedStream_ThrowsRatherThanReadingPhantomZeroBits()
    {
        byte[] input = [0x00, 0x04, 0x90];

        var error = Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(input, 2));

        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompress_ShorterThanRequested_ReportsHowFarItGot()
    {
        var error = Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(BlastExample, 64));

        Assert.Contains("13 of 64", error.Message, StringComparison.Ordinal);
    }
}