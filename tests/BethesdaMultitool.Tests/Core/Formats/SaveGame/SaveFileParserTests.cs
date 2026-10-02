using System.Text;
using BethesdaMultitool.Core.Formats.SaveGame.Reading;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SaveGame;

/// <summary>
///     Malformed-input regression tests for SaveFileParser.
///     Contract: Parse throws only InvalidDataException on truncated or size-lying input,
///     never ArgumentOutOfRangeException/IndexOutOfRangeException from unguarded reads.
///     Fixtures are raw FO3SAVEGAME payloads (no STFS wrapper) so they route down the
///     raw-magic branch of Parse.
/// </summary>
public class SaveFileParserTests
{
    private const string MagicText = "FO3SAVEGAME";
    private const byte Pipe = 0x7C;

    [Theory]
    [InlineData(null)]
    [InlineData("ENGLISH")]
    [InlineData("GERMAN")]
    public void Parse_HeaderWithOrWithoutLanguage_PreservesFollowingFields(string? language)
    {
        var save = SaveFileParser.Parse(BuildCompletePayload(language));
        Assert.Equal(language, save.Header.Language);
        Assert.Equal(1u, save.Header.ScreenshotWidth);
        Assert.Equal(1u, save.Header.ScreenshotHeight);
        Assert.Equal(183u, save.Header.SaveNumber);
        Assert.Equal("Courier", save.Header.PlayerName);
        Assert.Equal("Doc Mitchell's House", save.Header.PlayerCell);
        Assert.Equal(language is null ? 20 : 27, save.Header.FormVersion);
        Assert.Empty(save.Header.Plugins);
    }

    [Theory]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(0x80000000u, 2u)]
    [InlineData(512u, 288u)]
    public void Parse_ScreenshotSizeExceedsPayload_ReportsBounds(uint width, uint height)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            SaveFileParser.Parse(BuildCompletePayload("ENGLISH", width, height)));
        Assert.Contains("screenshot", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildCompletePayload(string? language, uint width = 1, uint height = 1)
    {
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            void UInt(uint value) { writer.Write(value); writer.Write(Pipe); }
            void Text(string value)
            {
                writer.Write((ushort)value.Length); writer.Write(Pipe);
                writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write(Pipe);
            }
            UInt(48);
            if (language != null)
            {
                var padded = new byte[64];
                Encoding.ASCII.GetBytes(language).CopyTo(padded, 0);
                writer.Write(padded); writer.Write(Pipe);
            }
            UInt(width); UInt(height); UInt(183);
            Text("Courier"); Text("Drifter"); UInt(1);
            Text("Doc Mitchell's House"); Text("000.05.05");
        }
        var prefix = BuildPayload((uint)header.Length, header.ToArray());
        var tableOffset = prefix.Length + 3 + 5 + 2;
        var payload = new byte[tableOffset + 128];
        prefix.CopyTo(payload, 0);
        payload[prefix.Length + 3] = language is null ? (byte)20 : (byte)27;
        BinaryTestWriter.WriteUInt32LE(payload, prefix.Length + 4, 2);
        payload[tableOffset - 1] = Pipe;
        BinaryTestWriter.WriteUInt32LE(payload, tableOffset, (uint)(tableOffset + 112));
        BinaryTestWriter.WriteUInt32LE(payload, tableOffset + 8, (uint)(tableOffset + 110));
        BinaryTestWriter.WriteUInt32LE(payload, tableOffset + 12, (uint)(tableOffset + 111));
        BinaryTestWriter.WriteUInt32LE(payload, tableOffset + 16, (uint)(tableOffset + 112));
        return payload;
    }

    /// <summary>
    ///     Build a raw payload: magic (11 bytes) + headerSize (uint32 LE at offset 11) + header fields.
    /// </summary>
    private static byte[] BuildPayload(uint headerSize, byte[] headerFields)
    {
        var payload = new byte[MagicText.Length + 4 + headerFields.Length];
        Encoding.ASCII.GetBytes(MagicText).CopyTo(payload, 0);
        BinaryTestWriter.WriteUInt32LE(payload, MagicText.Length, headerSize);
        headerFields.CopyTo(payload, MagicText.Length + 4);
        return payload;
    }

    /// <summary>
    ///     Write the four leading pipe-terminated uint32 header fields
    ///     (version, screenshotWidth, screenshotHeight, saveNumber) = 20 bytes.
    /// </summary>
    private static int WriteLeadingUInt32Fields(byte[] fields)
    {
        var pos = 0;
        for (var i = 0; i < 4; i++)
        {
            BinaryTestWriter.WriteUInt32LE(fields, pos, 1);
            fields[pos + 4] = Pipe;
            pos += 5;
        }

        return pos;
    }

    [Fact]
    public void Parse_TruncatedAfterMagic_ThrowsInvalidDataException()
    {
        // Magic + 2 bytes: the headerSize field itself is cut mid-read.
        var payload = new byte[MagicText.Length + 2];
        Encoding.ASCII.GetBytes(MagicText).CopyTo(payload, 0);

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("headerSize", ex.Message);
    }

    [Fact]
    public void Parse_HeaderSizeExceedsPayload_ThrowsInvalidDataException()
    {
        // headerSize lies (0x7FFFFFFF) — the declared header region cannot fit the payload.
        var payload = BuildPayload(0x7FFFFFFF, new byte[8]);

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("headerSize", ex.Message);
    }

    [Fact]
    public void Parse_TruncatedInUInt32Field_ThrowsInvalidDataException()
    {
        // headerSize=2 fits the payload, but the first uint32 header field (version)
        // needs 4 bytes and only 2 remain.
        var payload = BuildPayload(2, new byte[2]);

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void Parse_LenStringLengthExceedsPayload_ThrowsInvalidDataException()
    {
        // 4 uint fields (20 bytes), then a playerName length prefix claiming 0xFFFF bytes
        // with only 3 bytes left — previously sliced out of range.
        var fields = new byte[25];
        var pos = WriteLeadingUInt32Fields(fields);
        BinaryTestWriter.WriteUInt16LE(fields, pos, 0xFFFF);
        var payload = BuildPayload((uint)fields.Length, fields);

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("playerName", ex.Message);
    }

    [Fact]
    public void Parse_LenStringTruncatedLengthPrefix_ThrowsInvalidDataException()
    {
        // 4 uint fields (20 bytes), then a single byte where the 2-byte playerName
        // length prefix should be.
        var fields = new byte[21];
        WriteLeadingUInt32Fields(fields);
        var payload = BuildPayload((uint)fields.Length, fields);

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("playerName", ex.Message);
    }

    [Fact]
    public void Parse_GarbageBytes_ThrowsInvalidDataException()
    {
        // Not FO3SAVEGAME magic and not an STFS container.
        var payload = new byte[16];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0xA5 + i);
        }

        var ex = Assert.Throws<InvalidDataException>(() => SaveFileParser.Parse(payload));
        Assert.Contains("Not a valid save file", ex.Message);
    }
}
