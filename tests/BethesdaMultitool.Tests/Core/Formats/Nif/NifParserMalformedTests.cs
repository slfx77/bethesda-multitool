using BethesdaMultitool.Core.Formats.Nif.Parser;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Locks the <see cref="NifParser.Parse" /> malformed-input contract: unparseable input returns
///     null and NEVER throws — every render/export caller does <c>if (nif == null) return</c> with no
///     catch. Fixtures are minimal synthetic 20.2.0.7 headers (the NifConverterTests recipe) truncated
///     or corrupted at each header read the parser guards.
/// </summary>
public class NifParserMalformedTests
{
    // The standard 39-byte header line used by NifConverterTests.
    private static readonly byte[] HeaderLine = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();

    // Same line space-padded past Parse's 50-byte minimum so a fixture truncated inside the
    // version fields reaches the version-info guards instead of the too-short early return.
    private static readonly byte[] PaddedHeaderLine =
        "Gamebryo File Format, Version 20.2.0.7            \n"u8.ToArray();

    /// <summary>FNV-style header: 20.2.0.7, little-endian, user version 12, given block count.</summary>
    private static BinaryWriter StartNif(MemoryStream ms, uint blockCount = 0)
    {
        var bw = new BinaryWriter(ms);
        bw.Write(HeaderLine);
        bw.Write(0x14020007u); // binary version 20.2.0.7
        bw.Write((byte)1); // endian: little
        bw.Write(12u); // user version
        bw.Write(blockCount);
        return bw;
    }

    /// <summary>BSStreamHeader (bsVersion 34: three empty ExportStrings) + zero block types.</summary>
    private static void WriteBsStreamHeaderAndEmptyBlockTypes(BinaryWriter bw)
    {
        bw.Write(34u); // BS stream version (FNV)
        for (var i = 0; i < 3; i++) // Author / Process Script / Export Script, all empty
        {
            bw.Write((byte)1);
            bw.Write((byte)0);
        }

        bw.Write((ushort)0); // num block types
    }

    [Fact]
    public void Parse_TruncatedAfterHeaderString_ReturnsNull()
    {
        Assert.Null(NifParser.Parse(PaddedHeaderLine));
    }

    [Fact]
    public void Parse_TruncatedMidVersionInfo_ReturnsNull()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(PaddedHeaderLine);
        bw.Write(0x14020007u);
        bw.Write((byte)1); // endian byte, then truncated before User Version

        Assert.Null(NifParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_BlockCountAbsurd_ReturnsNull()
    {
        using var ms = new MemoryStream();
        using var bw = StartNif(ms, 0x7FFFFFFF);

        Assert.Null(NifParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_StringTableNumStringsLie_ReturnsNull()
    {
        using var ms = new MemoryStream();
        using var bw = StartNif(ms);
        WriteBsStreamHeaderAndEmptyBlockTypes(bw);
        bw.Write(1_000_000u); // num strings, with no bytes behind the claim
        bw.Write(0u); // max string length

        Assert.Null(NifParser.Parse(ms.ToArray()));
    }

    [Fact]
    public void Parse_StringTableStringLengthLie_ReturnsNull()
    {
        using var ms = new MemoryStream();
        using var bw = StartNif(ms);
        WriteBsStreamHeaderAndEmptyBlockTypes(bw);
        bw.Write(1u); // num strings
        bw.Write(0u); // max string length
        bw.Write(0xFFFFFFu); // string length far past EOF (and the 256-byte cap)

        Assert.Null(NifParser.Parse(ms.ToArray()));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]
    public void Parse_EmptyOrGarbage_ReturnsNull(byte[] data)
    {
        Assert.Null(NifParser.Parse(data));
    }

    /// <summary>Over-rejection control: the guards must not reject a minimal valid LE NIF.</summary>
    [Fact]
    public void Parse_MinimalValidLeNif_ReturnsInfo()
    {
        // Fully-authored zero-block header — the fixture family the string-table tests corrupt.
        using var ms = new MemoryStream();
        using var bw = StartNif(ms);
        WriteBsStreamHeaderAndEmptyBlockTypes(bw);
        bw.Write(0u); // num strings
        bw.Write(0u); // max string length
        bw.Write(0u); // num groups

        var info = NifParser.Parse(ms.ToArray());

        Assert.NotNull(info);
        Assert.Equal(0x14020007u, info.BinaryVersion);
        Assert.Equal(12u, info.UserVersion);
        Assert.False(info.IsBigEndian);
        Assert.Equal(0, info.BlockCount);
        Assert.Empty(info.Blocks);
        Assert.Empty(info.Strings);

        // The zero-padded NifConverterTests recipe must also stay accepted.
        var data = new byte[200];
        HeaderLine.CopyTo(data, 0);
        var pos = HeaderLine.Length;
        data[pos++] = 0x07; // version 0x14020007 LE
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        data[pos++] = 0x01; // endian: little
        data[pos] = 0x0C; // user version 12; everything after stays zero

        Assert.NotNull(NifParser.Parse(data));
    }
}