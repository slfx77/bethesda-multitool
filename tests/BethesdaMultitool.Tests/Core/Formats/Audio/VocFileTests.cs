using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Vectors for <see cref="VocFile" />. Shapes follow the retail Arena corpus (surveyed
///     2026-09-01: 76 files, all version 0x010A, all codec 0, block types 0/1/6/7, ten distinct
///     sample rates from 4000 to 11111 Hz).
/// </summary>
public class VocFileTests
{
    private const ushort Version = 0x010A;

    /// <summary>Builds a .VOC: 19-byte signature, EOF byte, u16 header size, u16 version, u16 checksum.</summary>
    private static byte[] BuildVoc(IEnumerable<byte> blocks, ushort version = Version, ushort? checksum = null)
    {
        var file = new List<byte>();
        file.AddRange("Creative Voice File"u8.ToArray());
        file.Add(0x1A);
        file.AddRange([26, 0]);
        file.AddRange([(byte)(version & 0xFF), (byte)(version >> 8)]);

        var check = checksum ?? (ushort)(~version + 0x1234);
        file.AddRange([(byte)(check & 0xFF), (byte)(check >> 8)]);

        file.AddRange(blocks);
        file.Add(0); // terminator
        return [.. file];
    }

    private static IEnumerable<byte> Block(byte type, params byte[] payload)
    {
        yield return type;
        yield return (byte)(payload.Length & 0xFF);
        yield return (byte)((payload.Length >> 8) & 0xFF);
        yield return (byte)((payload.Length >> 16) & 0xFF);
        foreach (var b in payload)
        {
            yield return b;
        }
    }

    /// <summary>Sound block: time constant, codec 0, then 8-bit unsigned samples.</summary>
    private static IEnumerable<byte> SoundBlock(byte timeConstant, params byte[] samples)
    {
        return Block(1, [timeConstant, 0, .. samples]);
    }

    [Fact]
    public void IsVoc_RequiresBothTheTextAndTheEofByte()
    {
        Assert.True(VocFile.IsVoc(BuildVoc(SoundBlock(131, 1, 2))));
        Assert.False(VocFile.IsVoc("Creative Voice File"u8.ToArray()));
        Assert.False(VocFile.IsVoc("Not a voice file at all"u8.ToArray()));
    }

    [Theory]
    // rate = 1000000 / (256 - timeConstant); these are the exact values the retail files produce.
    [InlineData(131, 8000)]
    [InlineData(6, 4000)]
    [InlineData(56, 5000)]
    [InlineData(166, 11111)]
    [InlineData(156, 10000)]
    public void Parse_DerivesSampleRateFromTheTimeConstant(byte timeConstant, int expectedRate)
    {
        var voc = VocFile.Parse(BuildVoc(SoundBlock(timeConstant, 1, 2, 3, 4)), "T.VOC");

        Assert.Equal(expectedRate, voc.SampleRate);
        Assert.Equal(8, voc.BitsPerSample);
        Assert.Equal(1, voc.Channels);
        Assert.Equal([1, 2, 3, 4], voc.Samples);
    }

    [Fact]
    public void Parse_RejectsABadHeaderChecksum()
    {
        // The header's own integrity rule: checksum == ~version + 0x1234.
        var ex = Assert.Throws<InvalidDataException>(
            () => VocFile.Parse(BuildVoc(SoundBlock(131, 1), checksum: 0x0000), "BAD.VOC"));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsNonVocInput()
    {
        Assert.Throws<InvalidDataException>(() => VocFile.Parse(new byte[64], "T.VOC"));
    }

    [Fact]
    public void Parse_ContinuationBlock_AppendsUnderThePreviousSettings()
    {
        var blocks = SoundBlock(131, 1, 2).Concat(Block(2, 3, 4, 5));

        var voc = VocFile.Parse(BuildVoc(blocks), "T.VOC");

        Assert.Equal(8000, voc.SampleRate);
        Assert.Equal([1, 2, 3, 4, 5], voc.Samples);
    }

    [Fact]
    public void Parse_SilenceBlock_EmitsMidScaleSamples()
    {
        // Run length is stored as count-1, and silence at 8-bit unsigned is 0x80, not 0.
        var blocks = SoundBlock(131, 1).Concat(Block(3, 2, 0, 131));

        var voc = VocFile.Parse(BuildVoc(blocks), "T.VOC");

        Assert.Equal([1, 0x80, 0x80, 0x80], voc.Samples);
    }

    [Fact]
    public void Parse_RepeatBlocks_AreRecordedAsMetadataNotUnrolled()
    {
        var blocks = Block(6, 3, 0).Concat(SoundBlock(131, 1, 2)).Concat(Block(7));

        var voc = VocFile.Parse(BuildVoc(blocks), "T.VOC");

        Assert.Equal(3, voc.RepeatCount);
        Assert.Equal([1, 2], voc.Samples);
    }

    [Fact]
    public void Parse_TextBlock_IsCapturedUpToItsTerminator()
    {
        var blocks = Block(5, [.. "hello"u8.ToArray(), 0, .. "junk"u8.ToArray()])
            .Concat(SoundBlock(131, 1));

        var voc = VocFile.Parse(BuildVoc(blocks), "T.VOC");

        Assert.Equal("hello", Assert.Single(voc.Texts));
    }

    [Fact]
    public void Parse_MarkerBlock_IsIgnored()
    {
        var blocks = Block(4, 7, 0).Concat(SoundBlock(131, 9));

        Assert.Equal([9], VocFile.Parse(BuildVoc(blocks), "T.VOC").Samples);
    }

    [Fact]
    public void Parse_ExtendedBlock_ConfiguresTheFollowingSoundBlock()
    {
        // Stereo, 16-bit time constant: rate = 256000000 / (channels * (65536 - tc)).
        // tc = 65024, channels = 2 -> 256000000 / (2 * 512) = 250000.
        var blocks = Block(8, 0x00, 0xFE, 0, 1).Concat(SoundBlock(0, 1, 2, 3, 4));

        var voc = VocFile.Parse(BuildVoc(blocks), "T.VOC");

        Assert.Equal(250000, voc.SampleRate);
        Assert.Equal(2, voc.Channels);
    }

    [Fact]
    public void Parse_NewFormatBlock_ReadsRateDepthAndChannelsDirectly()
    {
        // Type 9: u32 rate, u8 bits, u8 channels, u16 codec, 4 reserved, then samples.
        var payload = new byte[] { 0x44, 0xAC, 0, 0, 16, 2, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 };

        var voc = VocFile.Parse(BuildVoc(Block(9, payload)), "T.VOC");

        Assert.Equal(44100, voc.SampleRate);
        Assert.Equal(16, voc.BitsPerSample);
        Assert.Equal(2, voc.Channels);
        Assert.Equal([1, 2, 3, 4], voc.Samples);
        Assert.Equal(1, voc.FrameCount);
    }

    [Fact]
    public void Parse_CompressedCodec_IsRejectedRatherThanMisdecoded()
    {
        // Codec 1 is 4-bit ADPCM. No retail Arena effect uses one, and silently treating it as
        // PCM would produce noise that looks like a decode.
        var blocks = Block(1, 131, 1, 0xAA, 0xBB);

        Assert.Throws<NotSupportedException>(() => VocFile.Parse(BuildVoc(blocks), "T.VOC"));
    }

    [Fact]
    public void Parse_UnknownBlockType_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => VocFile.Parse(BuildVoc(Block(42, 1, 2)), "T.VOC"));
    }

    [Fact]
    public void Parse_BlockRunningPastEndOfFile_Throws()
    {
        var file = BuildVoc(SoundBlock(131, 1, 2)).ToList();

        // Inflate the sound block's 24-bit length so it claims more than the file holds.
        file[27] = 0xFF;
        file[28] = 0xFF;

        Assert.Throws<InvalidDataException>(() => VocFile.Parse([.. file], "T.VOC"));
    }

    [Fact]
    public void Parse_NoSoundBlocks_Throws()
    {
        Assert.Throws<InvalidDataException>(() => VocFile.Parse(BuildVoc(Block(4, 1, 0)), "T.VOC"));
    }

    [Fact]
    public void DurationSeconds_FollowsFrameCountAndRate()
    {
        var voc = VocFile.Parse(BuildVoc(SoundBlock(131, [.. Enumerable.Repeat((byte)7, 8000)])), "T.VOC");

        Assert.Equal(8000, voc.FrameCount);
        Assert.Equal(1.0, voc.DurationSeconds, 6);
    }
}
