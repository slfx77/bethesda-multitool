using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for <see cref="BosXboxSound" />: the Xbox release stores each sound as a complete
///     RIFF WAVE section, and its loader (<c>0x00081F90</c>) reads FIXED offsets 0x18 / 0x1C /
///     0x22 / 0x28 / 0x2C — the WAVE header's rate, average bytes per second, bits per sample,
///     data length and samples.
/// </summary>
public sealed class BosXboxSoundTests
{
    private static byte[] Chunk(string id, byte[] payload)
    {
        var b = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(b.AsSpan());
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(b.AsSpan(8));
        return b;
    }

    private static byte[] Fmt(ushort tag, ushort channels, int rate, ushort bits)
    {
        var blockAlign = (ushort)(channels * (bits / 8));
        var b = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(rate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), bits);
        return b;
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var body = chunks.SelectMany(c => c).ToArray();
        var b = new byte[12 + body.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b.AsSpan());
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(b.AsSpan(8));
        body.CopyTo(b.AsSpan(12));
        return b;
    }

    [Fact]
    public void ReadsTheFormatFieldsTheEnginesFixedOffsetsPointAt()
    {
        var samples = new byte[480];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)(i * 3);
        }

        var section = Riff(Chunk("fmt ", Fmt(1, 1, 24000, 16)), Chunk("data", samples));
        var sound = BosXboxSound.Parse(section, "synthetic");

        Assert.True(sound.IsCanonical);
        Assert.Equal(BosXboxSound.PcmFormatTag, sound.FormatTag);
        Assert.Equal(1, sound.Channels);
        Assert.Equal(24000, sound.SampleRate);
        Assert.Equal(48000, sound.AverageBytesPerSecond);
        Assert.Equal(2, sound.BlockAlign);
        Assert.Equal(16, sound.BitsPerSample);
        Assert.Equal(240, sound.SampleCount);
        Assert.Equal(0.01, sound.Duration, 6);

        // The engine's own reads: +0x18 rate, +0x1C average bytes, +0x22 bits, +0x28 length,
        // +0x2C samples. Pinned as raw offsets so a header laid out any other way would fail.
        Assert.Equal(24000U, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(0x18)));
        Assert.Equal(48000U, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(0x1C)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(0x22)));
        Assert.Equal((uint)samples.Length, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(0x28)));
        Assert.Equal(0x2C, sound.DataOffset);
        Assert.True(sound.Pcm(section).SequenceEqual(samples));
    }

    [Fact]
    public void ACanonicalSectionRoundTripsToItselfThroughToWave()
    {
        var section = Riff(Chunk("fmt ", Fmt(1, 1, 16000, 16)), Chunk("data", new byte[64]));
        var sound = BosXboxSound.Parse(section, "canonical");

        Assert.Equal(section, sound.ToWave(section));
    }

    [Fact]
    public void RecoversASectionWhoseAuthoringChunksDefeatTheEnginesFixedOffsets()
    {
        // ⚠⚠ Three retail sections (two in CRB_2_S.clp, one in MILL_4_S.clp) carry a Pro Tools
        // bext chunk before the format, so the engine's fixed +0x28 read lands inside it and
        // returns a data length of ZERO — the retail game plays them as silence. Walking the
        // chunks recovers them, which is exactly the difference this pins.
        var samples = new byte[128];
        samples[0] = 0x7F;
        var section = Riff(
            Chunk("bext", new byte[602]),
            Chunk("fmt ", Fmt(1, 1, 24000, 16)),
            Chunk("data", samples),
            Chunk("umid", new byte[64]));

        var sound = BosXboxSound.Parse(section, "bext");

        Assert.False(sound.IsCanonical);
        Assert.Equal(24000, sound.SampleRate);
        Assert.Equal(128, sound.DataLength);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(0x28)));

        var wave = sound.ToWave(section);
        Assert.Equal(BosXboxSound.CanonicalHeaderLength + 128, wave.Length);
        Assert.Equal((byte)0x7F, wave[BosXboxSound.CanonicalHeaderLength]);
        Assert.Equal(128U, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40)));
    }

    [Fact]
    public void RejectsASectionWhoseRiffSizeDisagreesWithItsLength()
    {
        var section = Riff(Chunk("fmt ", Fmt(1, 1, 16000, 16)), Chunk("data", new byte[16]));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), (uint)(section.Length - 12));

        Assert.False(BosXboxSound.IsSound(section));
        Assert.False(BosXboxSound.TryParse(section, "bad size", out _, out var error));
        Assert.Contains("RIFF", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsASectionWhoseChunkWalkDoesNotLandOnTheEnd()
    {
        var section = Riff(Chunk("fmt ", Fmt(1, 1, 16000, 16)), Chunk("data", new byte[16]));
        // Shorten the data chunk by 4 so the walk stops early with bytes left over.
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(40), 12);

        Assert.False(BosXboxSound.TryParse(section, "ragged", out _, out var error));
        Assert.Contains("chunk walk", error, StringComparison.Ordinal);
    }
}