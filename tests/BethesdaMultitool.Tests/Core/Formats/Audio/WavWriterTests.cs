using System;
using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Field-by-field checks on <see cref="WavWriter" />. Expected offsets and values come from the
///     RIFF/WAVE specification, not from the writer.
/// </summary>
public class WavWriterTests
{
    [Fact]
    public void BuildPcm_WritesTheCanonicalFortyFourByteHeader()
    {
        byte[] pcm = [1, 2, 3, 4, 5, 6, 7, 8];

        var wav = WavWriter.BuildPcm(pcm, sampleRate: 8000, bitsPerSample: 8, channels: 1);

        Assert.Equal(WavWriter.HeaderLength + pcm.Length, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("fmt "u8.ToArray(), wav[12..16]);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);

        // RIFF size counts everything after the size field itself.
        Assert.Equal((uint)(36 + pcm.Length), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal((uint)pcm.Length, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(pcm, wav[WavWriter.HeaderLength..]);
    }

    [Theory]
    [InlineData(8000, 8, 1, 8000, 1)]
    [InlineData(44100, 16, 2, 176400, 4)]
    [InlineData(22050, 16, 1, 44100, 2)]
    [InlineData(48000, 24, 2, 288000, 6)]
    public void BuildPcm_DerivesByteRateAndBlockAlign(
        int sampleRate,
        int bits,
        int channels,
        uint expectedByteRate,
        ushort expectedBlockAlign)
    {
        var wav = WavWriter.BuildPcm(new byte[expectedBlockAlign], sampleRate, bits, channels);

        Assert.Equal((ushort)channels, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal((uint)sampleRate, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(expectedByteRate, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(expectedBlockAlign, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal((ushort)bits, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
    }

    [Fact]
    public void BuildPcm_EmptyPayload_StillProducesAValidHeader()
    {
        var wav = WavWriter.BuildPcm([], 8000, 8, 1);

        Assert.Equal(WavWriter.HeaderLength, wav.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildPcm_NonPositiveSampleRate_Throws(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.BuildPcm([1], sampleRate, 8, 1));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(0)]
    public void BuildPcm_UnsupportedBitDepth_Throws(int bits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.BuildPcm([1], 8000, bits, 1));
    }

    [Fact]
    public void BuildPcm_NonPositiveChannelCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.BuildPcm([1], 8000, 8, 0));
    }

    [Fact]
    public void SavePcm_WritesAFileThatMatchesBuildPcm()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavwriter-{Guid.NewGuid():N}.wav");
        try
        {
            byte[] pcm = [10, 20, 30, 40];
            WavWriter.SavePcm(pcm, 11025, 8, 1, path);

            Assert.Equal(WavWriter.BuildPcm(pcm, 11025, 8, 1), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
