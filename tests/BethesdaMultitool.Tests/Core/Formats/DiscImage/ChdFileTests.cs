using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.DiscImage;
using BethesdaMultitool.Core.Formats.DiscImage.Chd;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     The native CHD reader against chdman-made fixtures (MAME 0.289, 2026-09-09) under
///     <c>tests/BethesdaMultitool.Tests/Resources/Chd/</c>: a 64 KiB raw image stored once per
///     codec (<c>zlib</c>, <c>lzma</c>, <c>huff</c>, <c>flac</c>), a 64 KiB sine-wave image
///     stored as <c>flac</c>, and the first 64 raw sectors of a real MODE1/2352 disc stored once
///     per CD codec (<c>cdzl</c>, <c>cdlz</c>, <c>cdfl</c>) — real sectors because chdman strips
///     only ECC that verifies, and regenerating it is the part of the CD path worth proving.
///     <para>
///         Two oracles per fixture, both external to this code: the SHA-1 of the input file the
///         fixture was made from, and the header's own raw SHA-1 (chdman's "Data SHA1"). A wrong
///         codec, a wrong CRC, a wrong ECC byte or a wrong byte order fails both.
///     </para>
/// </summary>
public sealed class ChdFileTests
{
    private const string Raw64kSha1 = "8fff6d0bce54a8bc8070715b11bafe4ba8c0f9f7";
    private const string Audio64kSha1 = "cf7638dba18cf717d9a181a1e85a6a21762bbaf3";
    private const string Cd64BinSha1 = "058205eab9958e97dc40b7cd57177d7d3e218318";

    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "tests", "BethesdaMultitool.Tests", "Resources", "Chd", name);
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        return path;
    }

    private static string Sha1Hex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    }

    [Theory]
    [InlineData("raw_zlib.chd", ChdFile.TagZlib)]
    [InlineData("raw_lzma.chd", ChdFile.TagLzma)]
    [InlineData("raw_huff.chd", ChdFile.TagHuff)]
    [InlineData("raw_flac.chd", ChdFile.TagFlac)]
    public void EveryRawCodecReproducesTheInputAndTheHeaderHash(string fixture, uint codec)
    {
        using var chd = ChdFile.Open(Fixture(fixture));

        Assert.Equal(65_536, chd.LogicalBytes);
        Assert.Equal(4096, chd.HunkBytes);
        Assert.Equal(2048, chd.UnitBytes);
        Assert.Equal(16, chd.HunkCount);
        Assert.Contains(codec, chd.Compressors);
        Assert.False(chd.IsCdShaped);
        Assert.False(chd.HasParent);

        var image = ReadAll(chd);
        Assert.Equal(Raw64kSha1, Sha1Hex(image));
        Assert.Equal(Raw64kSha1, chd.RawSha1Hex);
        Assert.Equal(chd.RawSha1Hex, Convert.ToHexString(chd.ComputeRawSha1()).ToLowerInvariant());
    }

    [Fact]
    public void FlacCodecDecodesAudioShapedDataThroughTheByteOrderMarker()
    {
        using var chd = ChdFile.Open(Fixture("audio_flac.chd"));

        Assert.Contains(ChdFile.TagFlac, chd.Compressors);
        var image = ReadAll(chd);
        Assert.Equal(Audio64kSha1, Sha1Hex(image));
        Assert.Equal(Audio64kSha1, chd.RawSha1Hex);
    }

    [Theory]
    [InlineData("cd64_cdzl.chd", ChdFile.TagCdZlib)]
    [InlineData("cd64_cdlz.chd", ChdFile.TagCdLzma)]
    [InlineData("cd64_cdfl.chd", ChdFile.TagCdFlac)]
    public void EveryCdCodecReproducesTheSectorsWithTheirEccRegenerated(string fixture, uint codec)
    {
        using var chd = ChdFile.Open(Fixture(fixture));

        Assert.Contains(codec, chd.Compressors);
        Assert.Equal(ChdFile.CdFrameSize, chd.UnitBytes);
        Assert.True(chd.IsCdShaped);
        var track = Assert.Single(chd.Tracks);
        Assert.Equal(1, track.Number);
        Assert.Equal("MODE1_RAW", track.Type);
        Assert.Equal(64, track.Frames);
        Assert.Equal(0, track.ChdFrameOffset);
        Assert.Equal(64L * ChdFile.CdFrameSize, chd.LogicalBytes);

        // The header hash covers the frames as stored (sector + 96 subcode bytes each).
        var image = ReadAll(chd);
        Assert.Equal(chd.RawSha1Hex, Sha1Hex(image));

        // The sectors alone reproduce the .bin they were cut from — including every ECC byte,
        // which chdman stripped and this reader put back.
        var sectors = new byte[64 * ChdFile.CdSectorSize];
        for (var frame = 0; frame < 64; frame++)
        {
            image.AsSpan(frame * ChdFile.CdFrameSize, ChdFile.CdSectorSize).CopyTo(sectors.AsSpan(frame * ChdFile.CdSectorSize));
        }

        Assert.Equal(Cd64BinSha1, Sha1Hex(sectors));
    }

    [Fact]
    public void CdSectorSourceServesTheIso9660DescriptorFromRawSectors()
    {
        using var chd = ChdFile.Open(Fixture("cd64_cdlz.chd"));
        using var source = new ChdSectorSource(chd);

        Assert.Equal(64, source.SectorCount);
        Assert.True(source.HasRawSectors);
        var region = Assert.Single(source.Tracks);
        Assert.Equal(0, region.StartLba);
        Assert.Equal(64, region.SectorCountValue);
        Assert.Equal(RawSectorSource.RawSectorSize, region.PhysicalSectorSize);
        Assert.False(region.IsAudio);

        var user = new byte[2048];
        var submode = source.ReadSector(16, user);
        Assert.Equal(0, submode);
        Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(user, 1, 5));

        var raw = new byte[RawSectorSource.RawSectorSize];
        Assert.Equal(RawSectorSource.RawSectorSize, source.ReadRawSector(16, raw));
        Assert.True(raw.AsSpan(0, 12).SequenceEqual(CdSectorEcc.SyncPattern));
        Assert.True(CdSectorEcc.Verify(raw));
    }

    [Fact]
    public void ChdStreamReadsAcrossHunkBoundariesAndSeeks()
    {
        using var chd = ChdFile.Open(Fixture("raw_lzma.chd"));
        using var stream = new ChdStream(chd, ownsFile: false);
        var whole = ReadAll(chd);

        Assert.Equal(65_536, stream.Length);
        var window = new byte[5000];
        stream.Position = 4096 - 1234;
        stream.ReadExactly(window);
        Assert.True(window.AsSpan().SequenceEqual(whole.AsSpan(4096 - 1234, 5000)));

        stream.Seek(-8, SeekOrigin.End);
        var tail = new byte[16];
        Assert.Equal(8, stream.Read(tail));
        Assert.True(tail.AsSpan(0, 8).SequenceEqual(whole.AsSpan(65_536 - 8)));
    }

    [Fact]
    public void ArchiveProbeClaimsACdShapedChd()
    {
        var path = Fixture("cd64_cdzl.chd");

        Assert.True(DiscImageBackend.TryProbe(path));
        Assert.False(XdvdfsVolume.TryProbe(path));
    }

    [Fact]
    public void ACorruptedHunkIsRefusedByItsCrc()
    {
        var path = Fixture("raw_zlib.chd");
        var bytes = File.ReadAllBytes(path);
        using var original = ChdFile.Open(path);

        // Flip a byte inside the first hunk's compressed data (after the 124-byte header, before
        // the map, which chdman writes at the end of the file).
        var corrupt = Path.Combine(Path.GetTempPath(), $"chd-corrupt-{Guid.NewGuid():N}.chd");
        try
        {
            bytes[ChdFile.HeaderSize + 8] ^= 0x55;
            File.WriteAllBytes(corrupt, bytes);
            using var damaged = ChdFile.Open(corrupt);
            var hunk = new byte[damaged.HunkBytes];
            Assert.ThrowsAny<InvalidDataException>(() => damaged.ReadHunk(0, hunk));
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    private static byte[] ReadAll(ChdFile chd)
    {
        var image = new byte[chd.LogicalBytes];
        var hunk = new byte[chd.HunkBytes];
        for (var i = 0; i < chd.HunkCount; i++)
        {
            chd.ReadHunk(i, hunk);
            var take = (int)Math.Min(chd.HunkBytes, chd.LogicalBytes - (long)i * chd.HunkBytes);
            hunk.AsSpan(0, take).CopyTo(image.AsSpan(i * chd.HunkBytes));
        }

        return image;
    }
}
