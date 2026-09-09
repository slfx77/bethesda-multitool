using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Opt-in checks of the TES Arena v1.04 floppy installer container (<c>RUN_BUCKET_B=1</c>),
///     staged one directory per disk.
///     <para>
///         Two independent oracles are pinned. The container's own: the 106 file records sum to
///         20,513,662, which is the dword <c>ARENA.TDS</c> carries — a value the directory does not
///         contain — and the block walk consumes all 11,083,470 bytes of the concatenated
///         <c>ARENA.1..8</c>. And an EXTERNAL one: four of the decoded files must be byte-identical
///         to the same files in a retail install, whose MD5s are pinned as literals here. A codec or
///         framing error large enough to matter cannot leave a 16.8 MB GLOBAL.BSA digest intact.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ArenaInstallerRetailTests
{
    /// <summary>MD5s of the installed counterparts of four v1.04 archive members.</summary>
    private static readonly (string Name, long Size, string Md5)[] IdenticalToInstall =
    [
        ("GLOBAL.BSA", 16_802_136, "397de2bfc678e98347a8727b35299406"),
        ("TEMPLATE.DAT", 395_981, "e29d095233affad785519ceea060b2f7"),
        ("FONT_B.DAT", 1_295, "e301d0823e730453b4f5d4637e1f093e"),
        ("ULTRAMID.EXE", 31_812, "d051082e529527804a368532f01ab4f5")
    ];

    // MD5 here is a FILE IDENTITY check against a published digest, not a security primitive.
#pragma warning disable CA5351
    private static string Md5(byte[] bytes)
    {
        return Convert.ToHexStringLower(MD5.HashData(bytes));
    }
#pragma warning restore CA5351

    private static string RequireAnchor()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var anchor = RealAssetPaths.Classics.ArenaFloppyInstallerAnchor();
        Assert.SkipWhen(anchor is null, RealAssetPaths.SkipMessage("Arena v1.04 floppy ARENA.H1"));
        return anchor;
    }

    [Fact]
    public void Parse_RetailRelease_DirectorySumsToTdsAndTheBlockWalkIsExact()
    {
        var anchor = RequireAnchor();

        Assert.True(ArenaInstallerArchive.TryProbe(anchor));
        var archive = ArenaInstallerArchive.Parse(anchor);

        Assert.Equal(8, archive.HeaderVolumes.Count);
        Assert.Equal(8, archive.DataVolumes.Count);
        Assert.Equal("ARENA", archive.RootDirectory);
        Assert.Equal(106, archive.Entries.Count);
        Assert.Equal(20_513_662, archive.TotalSize);
        Assert.Equal(20_513_662, archive.Entries.Sum(entry => entry.Size));
        Assert.Equal(11_083_470, archive.DataStream.Length);

        Assert.Equal(2_115, archive.BlockCount);
        Assert.Equal(42, archive.StoredBlockCount);
        Assert.Equal(4, archive.EmptyBlockCount);

        // The four empty terminators belong to the four files whose size is a multiple of 10,000
        // (the literal, not the production constant — a fixture that moves with the code under test
        // cannot fail).
        Assert.Equal(4, archive.Entries.Count(entry => entry.Size % 10_000 == 0));

        // The release's own size: the eight header volumes (10,272 bytes of 96-byte records), the
        // eight data volumes (11,083,470) and ARENA.TDS's single dword. `archive info` reports THIS
        // and not the 8,160-byte anchor the file system sees for ARENA.H1.
        Assert.Equal(10_272, archive.DirectoryBytes);
        Assert.Equal(11_093_746, archive.ContainerSizeBytes);
    }

    [Fact]
    public void Extract_ReproducesTheInstalledFilesByteForByte()
    {
        var anchor = RequireAnchor();
        var archive = ArenaInstallerArchive.Parse(anchor);

        foreach (var (name, size, md5) in IdenticalToInstall)
        {
            var entry = archive.Find(name);
            Assert.NotNull(entry);
            Assert.Equal(size, entry.Size);

            var bytes = archive.Extract(entry);
            Assert.Equal(size, bytes.Length);
            Assert.Equal(md5, Md5(bytes));
        }
    }

    [Fact]
    public void Extract_EveryFile_DecodesToItsDeclaredSize()
    {
        var anchor = RequireAnchor();
        var archive = ArenaInstallerArchive.Parse(anchor);

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += archive.Extract(entry).Length;
        }

        Assert.Equal(20_513_662, total);
        Assert.Equal(archive.TotalSize, total);
    }

    [Fact]
    public void ArchiveReader_ClaimsTheReleaseAndExtractsThroughTheSeam()
    {
        var anchor = RequireAnchor();

        using var reader = ArchiveReader.Open(anchor);

        Assert.Equal("Install Utility (Arena)", reader.FormatName);
        Assert.Equal(106, reader.TotalFiles);
        Assert.Equal(11_093_746, reader.ContainerSizeBytes);

        var entry = reader.ListFiles().Single(file =>
            string.Equals(file.Name, "GLOBAL.BSA", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ARENA/GLOBAL.BSA", entry.FullPath);

        Assert.Equal(
            IdenticalToInstall[0].Md5, Md5(reader.Extract(entry)));
    }
}