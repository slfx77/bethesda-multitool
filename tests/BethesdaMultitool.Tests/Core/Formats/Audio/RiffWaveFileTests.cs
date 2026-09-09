using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Vectors for RIFF/WAVE passthrough, shaped after Battlespire's SPIRE.SND measured
///     2026-09-06: 370 entries, all PCM 8-bit mono, and NINE that over-declare their RIFF length
///     by one byte.
/// </summary>
public sealed class RiffWaveFileTests
{
    /// <summary>Builds a minimal PCM wave; <paramref name="declaredBias" /> perturbs the RIFF length.</summary>
    private static byte[] Wave(int sampleRate = 11025, int bits = 8, int channels = 1, int samples = 4,
        int declaredBias = 0, int formatTag = 1)
    {
        var riff = new byte[44 + samples];
        "RIFF"u8.CopyTo(riff);
        BitConverter.GetBytes(riff.Length - 8 + declaredBias).CopyTo(riff, 4);
        "WAVE"u8.CopyTo(riff.AsSpan(8));
        "fmt "u8.CopyTo(riff.AsSpan(12));
        BitConverter.GetBytes(16).CopyTo(riff, 16);
        BitConverter.GetBytes((ushort)formatTag).CopyTo(riff, 20);
        BitConverter.GetBytes((ushort)channels).CopyTo(riff, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(riff, 24);
        BitConverter.GetBytes(sampleRate * channels * bits / 8).CopyTo(riff, 28);
        BitConverter.GetBytes((ushort)(channels * bits / 8)).CopyTo(riff, 32);
        BitConverter.GetBytes((ushort)bits).CopyTo(riff, 34);
        "data"u8.CopyTo(riff.AsSpan(36));
        BitConverter.GetBytes(samples).CopyTo(riff, 40);
        return riff;
    }

    [Fact]
    public void IsRiffWave_RecognisesTheSignatureAndRejectsOthers()
    {
        Assert.True(RiffWaveFile.IsRiffWave(Wave()));
        Assert.False(RiffWaveFile.IsRiffWave("Creative Voice File"u8.ToArray()));
        Assert.False(RiffWaveFile.IsRiffWave([1, 2, 3]));
    }

    [Fact]
    public void Parse_ReadsTheFormatChunk()
    {
        var wave = RiffWaveFile.Parse(Wave(32000), "89");

        Assert.Equal((32000, 8, 1), (wave.SampleRate, wave.BitsPerSample, wave.Channels));
        Assert.Equal("89", wave.Name);
    }

    [Fact]
    public void Parse_RepairsALengthThatOverDeclaresByThePadByte()
    {
        // ⚠ The measured case: 9 of SPIRE.SND's 370 declare one byte more than is present, because
        // RIFF pads odd chunks and the archive stores the unpadded bytes. A strict reader rejects
        // exactly those nine; the audio in them is complete.
        var wave = RiffWaveFile.Parse(Wave(declaredBias: 1), "18");

        var declared = BitConverter.ToUInt32(wave.Riff, 4);
        Assert.Equal((uint)(wave.Riff.Length - RiffWaveFile.HeaderLength), declared);
    }

    [Fact]
    public void Parse_RepairsALengthThatUnderDeclares()
    {
        var wave = RiffWaveFile.Parse(Wave(declaredBias: -4), "x");

        Assert.Equal((uint)(wave.Riff.Length - RiffWaveFile.HeaderLength), BitConverter.ToUInt32(wave.Riff, 4));
    }

    [Fact]
    public void Parse_LeavesAWellFormedFileByteIdentical()
    {
        var original = Wave();
        var wave = RiffWaveFile.Parse(original, "ok");

        Assert.Equal(original, wave.Riff);
    }

    [Fact]
    public void Parse_RejectsANonPcmFormat()
    {
        // Passthrough is only honest for PCM; a compressed payload needs a decoder, not a copy.
        Assert.Throws<NotSupportedException>(() => RiffWaveFile.Parse(Wave(formatTag: 17), "adpcm"));
    }

    [Fact]
    public void Parse_RejectsBytesThatAreNotRiff()
    {
        Assert.Throws<InvalidDataException>(() => RiffWaveFile.Parse("not a wave!!"u8.ToArray(), "bad"));
    }

    [Fact]
    public void Parse_WithNoFormatChunk_Throws()
    {
        var riff = Wave();
        "junk"u8.CopyTo(riff.AsSpan(12)); // blank out the fmt tag

        Assert.Throws<InvalidDataException>(() => RiffWaveFile.Parse(riff, "nofmt"));
    }

    [Fact]
    public void DurationSeconds_FollowsTheFormat()
    {
        var wave = RiffWaveFile.Parse(Wave(8, 8, 1, 16), "d");

        Assert.Equal(2.0, wave.DurationSeconds, 3);
    }
}
