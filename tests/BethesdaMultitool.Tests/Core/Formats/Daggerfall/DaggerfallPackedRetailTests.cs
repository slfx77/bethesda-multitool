using System.Security.Cryptography;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Opt-in checks of the 1996 Daggerfall CD's <c>ARENA2\PACKED.DAT</c> (<c>RUN_BUCKET_B=1</c>).
///     <para>
///         The decisive expectation is an EXTERNAL oracle: the two files the container reconstructs
///         must be byte-identical to the loose <c>ARCH3D.BSA</c> and <c>DAGGER.SND</c> of a patched
///         retail install, whose MD5s are pinned here as literals. Nothing about those digests can
///         be produced by this repo's code — if the DCL framing were off by a byte, or the block
///         order wrong, or a block's slack mis-measured, the digest would differ. The container
///         tiling was already exact while the payload was still undecoded, so the tiling alone
///         could NOT have settled the codec; these digests are what settle it.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallPackedRetailTests
{
    /// <summary>MD5 of a retail install's loose <c>ARENA2\ARCH3D.BSA</c>.</summary>
    private const string Arch3DMd5 = "26d3e9359561d5e105daadc4d04871e7";

    /// <summary>MD5 of a retail install's loose <c>ARENA2\DAGGER.SND</c>.</summary>
    private const string DaggerSndMd5 = "37c10bc73bb0c6184098900d26e37a7e";

    private static string RequirePackedDat()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.Classics.DaggerfallCdPackedDat();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage("Daggerfall CD ARENA2/PACKED.DAT"));
        return path;
    }

    [Fact]
    public void Parse_RetailContainer_TilesExactly()
    {
        var path = RequirePackedDat();

        Assert.True(DaggerfallPackedArchive.TryProbe(path));
        var archive = DaggerfallPackedArchive.Parse(path);

        Assert.Equal("ARENA2", archive.TargetDirectory);
        Assert.Equal(2, archive.Entries.Count);

        var arch3d = archive.Entries[0];
        Assert.Equal("ARCH3D.BSA", arch3d.Name);
        Assert.Equal(27_143_532, arch3d.UncompressedSize);
        Assert.Equal(DaggerfallPackedArchive.FirstBlockOffset, arch3d.FirstBlockOffset);
        Assert.Equal(104, arch3d.Blocks.Count);

        var sound = archive.Entries[1];
        Assert.Equal("DAGGER.SND", sound.Name);
        Assert.Equal(7_661_766, sound.UncompressedSize);
        Assert.Equal(6_354_892, sound.FirstBlockOffset);
        Assert.Equal(30, sound.Blocks.Count);

        // Every block but each stream's last carries a full 0x40000 bytes; the remainders are the
        // sizes mod the block size, which is what makes 104 and 30 the ceilings rather than a count
        // read out of the file.
        Assert.All(arch3d.Blocks.Take(103), block =>
            Assert.Equal(DaggerfallPackedArchive.BlockSize, block.UncompressedSize));
        Assert.Equal(142_700, arch3d.Blocks[^1].UncompressedSize);
        Assert.All(sound.Blocks.Take(29), block =>
            Assert.Equal(DaggerfallPackedArchive.BlockSize, block.UncompressedSize));
        Assert.Equal(59_590, sound.Blocks[^1].UncompressedSize);
    }

    /// <summary>
    ///     ⚠⚠ The refutation of a claim this track once published: "the blocks carry no end-of-stream
    ///     code, and each leaves exactly 2 declared bytes unread — the encoder's flush". Decoding one
    ///     symbol further shows every block closing on the codec's 519 end code, emitting nothing
    ///     more and leaving 0 bytes. That was an artefact of stopping the decode the instant the
    ///     declared uncompressed size was reached. Under the refuted reading this test fails on the
    ///     FIRST block; it is also the check that makes the declared compressed size VERIFIED rather
    ///     than merely bounding the payload.
    /// </summary>
    [Fact]
    public void EveryBlockClosesOnItsEndCodeAndConsumesItsDeclaredCompressedSize()
    {
        var path = RequirePackedDat();
        var archive = DaggerfallPackedArchive.Parse(path);
        var bytes = File.ReadAllBytes(path);

        var blocks = 0;
        foreach (var block in archive.Entries.SelectMany(entry => entry.Blocks))
        {
            var compressed = bytes.AsSpan((int)block.DataOffset, block.CompressedSize);
            PkwareDclDecoder.Decompress(compressed, block.UncompressedSize, out var consumed);
            Assert.Equal(block.CompressedSize, consumed);
            blocks++;
        }

        Assert.Equal(134, blocks);
    }

    [Fact]
    public void Extract_ReproducesTheInstalledFilesByteForByte()
    {
        var path = RequirePackedDat();
        var archive = DaggerfallPackedArchive.Parse(path);
        var bytes = File.ReadAllBytes(path);

        byte[] Read(long offset, int count)
        {
            return bytes.AsSpan((int)offset, count).ToArray();
        }

        var arch3d = DaggerfallPackedArchive.Extract(archive.Entries[0], Read);
        var sound = DaggerfallPackedArchive.Extract(archive.Entries[1], Read);

        Assert.Equal(27_143_532, arch3d.Length);
        Assert.Equal(Arch3DMd5, Md5(arch3d));
        Assert.Equal(7_661_766, sound.Length);
        Assert.Equal(DaggerSndMd5, Md5(sound));
    }

    /// <summary>
    ///     ⚠ WHERE the end code sits, which two successive notes in this track got wrong. It is 16
    ///     bits — the match flag, seven 0 bits (length symbol 15, base 264) and eight 1 bits of extra
    ///     — and it ENDS on the payload's last byte, so it occupies the last two bytes only when it
    ///     happens to be byte aligned. Packed LSB-first with the trailing bits of the final byte
    ///     zero, the last two bytes therefore read as that code at one of eight bit offsets, and the
    ///     census below is the retail distribution: byte aligned (<c>01 FF</c>) on 14 blocks, the
    ///     other 120 starting in the third-from-last byte. A reading that said "the end code consumes
    ///     the last two bytes exactly" predicts <c>01ff</c> on all 134 and fails here; a block whose
    ///     tail were anything but these eight values would fail too.
    /// </summary>
    [Fact]
    public void TheEndCodeIsByteAlignedOnFourteenBlocksAndSpansThreeBytesOnTheRest()
    {
        var path = RequirePackedDat();
        var archive = DaggerfallPackedArchive.Parse(path);
        var bytes = File.ReadAllBytes(path);

        var census = new Dictionary<int, int>();
        foreach (var block in archive.Entries.SelectMany(entry => entry.Blocks))
        {
            var end = (int)block.DataOffset + block.CompressedSize;
            var tail = (bytes[end - 2] << 8) | bytes[end - 1];
            census[tail] = census.GetValueOrDefault(tail) + 1;
        }

        // 0x01FF is the aligned code; the rest are it shifted by 1..7 bits into the two bytes.
        Assert.Equal(
            new Dictionary<int, int>
            {
                [0x01FF] = 14,
                [0x807F] = 9,
                [0xC03F] = 18,
                [0xE01F] = 24,
                [0xF00F] = 22,
                [0xF807] = 14,
                [0xFC03] = 15,
                [0xFE01] = 18
            },
            census);
        Assert.Equal(134, census.Values.Sum());
    }

    [Fact]
    public void ArchiveReader_ClaimsTheContainerAndExtractsThroughTheSeam()
    {
        var path = RequirePackedDat();

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("PACKED.DAT (Daggerfall)", reader.FormatName);
        Assert.Equal(2, reader.TotalFiles);

        var entries = reader.ListFiles();
        Assert.Equal(
            new[] { "ARENA2/ARCH3D.BSA", "ARENA2/DAGGER.SND" },
            entries.Select(entry => entry.FullPath).ToArray());

        Assert.Equal(DaggerSndMd5, Md5(reader.Extract(entries[1])));
    }

    // MD5 here is a FILE IDENTITY check against a published digest, not a security primitive.
#pragma warning disable CA5351
    private static string Md5(byte[] bytes)
    {
        return Convert.ToHexStringLower(MD5.HashData(bytes));
    }
#pragma warning restore CA5351
}