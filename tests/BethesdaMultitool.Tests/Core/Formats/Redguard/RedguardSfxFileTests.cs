using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for Redguard's <c>MAIN.SFX</c> sound bank, shaped after the retail file
///     measured by independent Python walks 2026-09-04 (118 sounds; 22,050 Hz ×106 / 11,025 ×12;
///     16-bit ×105 / 8-bit ×13; 112.5 s).
///     <para>
///         The two facts most easily got wrong are pinned outright: the bank follows Redguard's
///         house IFF style where chunk LENGTHS are BIG-endian while the values inside are
///         little-endian, and <b>the sample depth is PER RECORD</b> — reading the first record's
///         depth and applying it to the bank mangles 13 of the 118.
///     </para>
/// </summary>
public sealed class RedguardSfxFileTests
{
    /// <summary>A chunk in the house style: 4-char tag, BIG-endian length, payload.</summary>
    private static byte[] Chunk(string tag, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        return [.. Encoding.ASCII.GetBytes(tag), .. length, .. body];
    }

    /// <summary>One sound record: a 27-byte header then its samples.</summary>
    private static byte[] Sound(int sampleRate, int depthFlag, byte[] samples)
    {
        var header = new byte[RedguardPcmHeader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.DepthFlagOffset), (uint)depthFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.SampleRateOffset), (uint)sampleRate);
        header[12] = 100;

        // ⚠ The length sits at the UNALIGNED offset 22 — not on a 4-byte boundary.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.LengthOffset), (uint)samples.Length);
        return [.. header, .. samples];
    }

    private static byte[] Bank(string banner, params byte[][] sounds)
    {
        var head = new byte[RedguardSfxFile.BannerLength + 4];
        Encoding.ASCII.GetBytes(banner).CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(RedguardSfxFile.BannerLength), (uint)sounds.Length);

        var body = sounds.SelectMany(s => s).ToArray();
        return [.. Chunk("FXHD", head), .. Chunk("FXDT", body), .. Encoding.ASCII.GetBytes("END "), 0, 0, 0, 0];
    }

    [Fact]
    public void IsSfxFile_RecognisesTheBankAndRejectsOthers()
    {
        Assert.True(RedguardSfxFile.IsSfxFile(Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[8]))));
        Assert.False(RedguardSfxFile.IsSfxFile("RIFFxxxxWAVE"u8.ToArray()));
    }

    [Fact]
    public void Parse_ReadsTheBannerAndEverySound()
    {
        var bank = RedguardSfxFile.Parse(
            Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[8]), Sound(11025, 0, new byte[4])),
            "MAIN.SFX");

        Assert.Equal(RedguardSfxFile.RetailBanner, bank.Banner);
        Assert.Equal(2, bank.Sounds.Count);
        Assert.Equal(0, bank.Sounds[0].Index);
        Assert.Equal(1, bank.Sounds[1].Index);
    }

    [Fact]
    public void Parse_TakesTheDepthPerRecordNotFromTheFirstOne()
    {
        // ⚠ THE trap. 13 of the retail bank's 118 are 8-bit while the rest are 16-bit. Reading
        // record 0's depth and applying it bank-wide halves or doubles the length of those 13 —
        // which was caught originally by five odd-length records.
        var bank = RedguardSfxFile.Parse(
            Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[8]), Sound(22050, 0, new byte[5])),
            "MAIN.SFX");

        Assert.Equal(16, bank.Sounds[0].BitsPerSample);
        Assert.Equal(8, bank.Sounds[1].BitsPerSample);
        Assert.Equal(2, bank.Sounds[0].BytesPerFrame);
        Assert.Equal(1, bank.Sounds[1].BytesPerFrame);
    }

    [Fact]
    public void Parse_ReadsTheSampleRatePerRecord()
    {
        var bank = RedguardSfxFile.Parse(
            Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[8]), Sound(11025, 1, new byte[8])),
            "MAIN.SFX");

        Assert.Equal(22050, bank.Sounds[0].SampleRate);
        Assert.Equal(11025, bank.Sounds[1].SampleRate);
    }

    [Fact]
    public void Sound_DurationFollowsRateAndDepth()
    {
        // 16-bit at 22,050 Hz: 44,100 bytes is one second.
        var bank = RedguardSfxFile.Parse(
            Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[44_100])), "MAIN.SFX");

        Assert.Equal(1.0, bank.Sounds[0].DurationSeconds, 3);
    }

    [Fact]
    public void Parse_CarriesTheSamplesThrough()
    {
        var samples = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var bank = RedguardSfxFile.Parse(Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, samples)), "MAIN.SFX");

        Assert.Equal(samples, bank.Sounds[0].Samples.ToArray());
    }

    [Fact]
    public void Parse_RejectsBytesThatAreNotABank()
    {
        Assert.Throws<InvalidDataException>(
            () => RedguardSfxFile.Parse("not a sound bank at all"u8.ToArray(), "BAD.SFX"));
    }

    [Fact]
    public void Parse_ChunkLengthsAreBigEndian()
    {
        // The house style: BIG-endian chunk lengths wrapping little-endian values. A
        // little-endian length reading finds a wildly wrong chunk size and cannot reach the sounds.
        var bank = Bank(RedguardSfxFile.RetailBanner, Sound(22050, 1, new byte[8]));

        var declared = BinaryPrimitives.ReadUInt32BigEndian(bank.AsSpan(4));
        Assert.Equal((uint)(RedguardSfxFile.BannerLength + 4), declared);
    }
}
