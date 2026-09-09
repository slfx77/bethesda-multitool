using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.DiscImage;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for <see cref="XdvdfsVolume" /> and <see cref="XdvdfsBackend" />, shaped
///     after the Fallout: Brotherhood of Steel Xbox disc surveyed 2026-09-08 (491 files, 67
///     directories, a 404-byte root table at sector 264).
///     <para>
///         The probe leans on a magic that appears TWICE in the descriptor sector and on a walk
///         that must consume every directory table exactly, so the rejection cases carry as much
///         weight as the accept ones: an entry the binary tree never links to is left sitting in
///         what should be filler, and that is what the tiling gate catches.
///     </para>
/// </summary>
public sealed class XdvdfsArchiveTests : IDisposable
{
    private const int SectorSize = 2048;
    private const int DescriptorSector = 32;
    private const int RootSector = 33;
    private const int ResxSector = 34;
    private const int C1Sector = 35;
    private const int BarSector = 36;
    private const int XbeSector = 37;
    private const int AllDdfSector = 38;
    private const int BarDdfSector = 39;
    private const int TotalSectors = 40;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    private string WriteTemp(byte[] bytes, string extension = ".iso")
    {
        var path = Path.Combine(Path.GetTempPath(), $"xdvdfs-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    ///     A three-file, four-directory-table image mirroring the retail tree's shape:
    ///     <c>default.xbe</c> beside <c>resx</c>, and <c>resx/c1/BAR/BAR.ddf</c> three levels down.
    ///     Every table is a whole 2,048-byte sector of <c>0xFF</c> filler with its entries written
    ///     over the front, which is how the real tables are laid out.
    /// </summary>
    private static byte[] BuildImage()
    {
        var image = new byte[TotalSectors * SectorSize];

        // Descriptor: magic, root sector, root size, FILETIME, then the magic again at +0x7EC.
        var descriptor = DescriptorSector * SectorSize;
        Magic.CopyTo(image, descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(descriptor + 20), RootSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(descriptor + 24), SectorSize);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(descriptor + 28), 0x01c38a2023da6e10);
        Magic.CopyTo(image, descriptor + 0x7EC);

        // Root: "resx" (a directory) with "default.xbe" as its right subtree.
        FillTable(image, RootSector);
        WriteEntry(image, RootSector, 0, 0, 20 / 4, ResxSector, SectorSize, 0x10, "resx");
        WriteEntry(image, RootSector, 20, 0, 0, XbeSector, 100, 0x20, "default.xbe");

        // resx: "all.ddf" with "c1" as its right subtree.
        FillTable(image, ResxSector);
        WriteEntry(image, ResxSector, 0, 0, 24 / 4, AllDdfSector, 50, 0x20, "all.ddf");
        WriteEntry(image, ResxSector, 24, 0, 0, C1Sector, SectorSize, 0x10, "c1");

        FillTable(image, C1Sector);
        WriteEntry(image, C1Sector, 0, 0, 0, BarSector, SectorSize, 0x10, "BAR");

        FillTable(image, BarSector);
        WriteEntry(image, BarSector, 0, 0, 0, BarDdfSector, 30, 0x20, "BAR.ddf");

        FillPayload(image, XbeSector, 100, 0xA1);
        FillPayload(image, AllDdfSector, 50, 0xB2);
        FillPayload(image, BarDdfSector, 30, 0xC3);
        return image;
    }

    private static void FillTable(byte[] image, int sector)
    {
        image.AsSpan(sector * SectorSize, SectorSize).Fill(0xFF);
    }

    private static void FillPayload(byte[] image, int sector, int size, byte seed)
    {
        for (var i = 0; i < size; i++)
        {
            image[sector * SectorSize + i] = (byte)(seed + i);
        }
    }

    private static void WriteEntry(
        byte[] image, int sector, int offset, int left, int right, uint startSector, uint size,
        byte attributes, string name)
    {
        var at = sector * SectorSize + offset;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), (ushort)left);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 2), (ushort)right);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 4), startSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 8), size);
        image[at + 12] = attributes;
        image[at + 13] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(image, at + 14);
    }

    [Fact]
    public void TheProbeChainMountsAnXdvdfsImageAndFlattensItsNestedDirectories()
    {
        var path = WriteTemp(BuildImage());
        Assert.True(XdvdfsBackend.TryProbe(path));

        using var reader = ArchiveReader.Open(path);

        // ArchiveProbe must reach the XDVDFS step, not the ISO9660 one behind it.
        Assert.Equal("XDVDFS (Xbox disc)", reader.FormatName);
        Assert.Equal(3, reader.TotalFiles);
        Assert.Equal(
            new[] { "default.xbe", "resx/all.ddf", "resx/c1/BAR/BAR.ddf" },
            reader.ListFiles().Select(e => e.FullPath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AnEntryExtractsTheBytesAtItsSectorExtent()
    {
        var path = WriteTemp(BuildImage());
        using var reader = ArchiveReader.Open(path);

        var entry = reader.ListFiles().Single(e => e.FullPath == "resx/c1/BAR/BAR.ddf");
        Assert.Equal(30L, entry.Size);
        Assert.Equal((long)BarDdfSector * SectorSize, entry.Offset);

        var payload = reader.Extract(entry);
        Assert.Equal(30, payload.Length);
        Assert.Equal((byte)0xC3, payload[0]);
        Assert.Equal((byte)(0xC3 + 29), payload[29]);
    }

    [Fact]
    public void TheDirectoryTablesAreAccountedForByteForByte()
    {
        var path = WriteTemp(BuildImage());
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var volume = XdvdfsVolume.Read(stream);

        // Four tables of one sector each; the entries are 14 bytes plus their names, so the
        // accounting is fixed by the fixture and not by the reader:
        //   resx 18, default.xbe 25, all.ddf 21, c1 16, BAR 17, BAR.ddf 21 = 118.
        Assert.Equal(4, volume.DirectoryCount);
        Assert.Equal(4L * SectorSize, volume.DirectoryTableBytes);
        Assert.Equal(118L, volume.DirectoryEntryBytes);
        Assert.Equal(4L * SectorSize - 118, volume.DirectoryFillerBytes);
        Assert.Equal(0L, volume.PartitionOffset);

        // Table depth counts the ROOT table as 0, so this fixture's root → resx → c1 → BAR is 3.
        // Fixed by the fixture: the chain is four tables long by construction. The convention is
        // pinned here because the retail pins (3 on the BOS disc, 4 on the X360 dump) mean nothing
        // without it — an off-by-one in the convention was how "3 on either disc" got published.
        Assert.Equal(3, volume.MaxTableDepth);

        // The FILETIME the fixture carries is the retail disc's own, copied byte for byte.
        Assert.Equal(
            "2003-10-04 02:35:14",
            volume.CreatedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnEntryTheTreeNeverLinksToBreaksTheTilingGate()
    {
        var image = BuildImage();

        // A perfectly well-formed entry, written into the root table's filler but linked from
        // nothing. Nothing about the entry itself is wrong — only that the walk cannot reach it,
        // which is exactly the failure a "walk the tree and stop" reader would not notice.
        WriteEntry(image, RootSector, 64, 0, 0, AllDdfSector, 50, 0x20, "orphan.dat");

        var path = WriteTemp(image);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<InvalidDataException>(() => XdvdfsVolume.Read(stream));
        Assert.Contains("does not consume the table exactly", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The filler gate demands <c>0xFF</c> and nothing else. It used to allow up to three ZERO
    ///     bytes as a supposed 4-byte alignment pad — an allowance that was assumed rather than
    ///     measured, and that the measurement refutes: over both retail XDVDFS images the gap-byte
    ///     histogram is <c>{0xFF: 204,056}</c> with not one zero, inter-entry pads included. This
    ///     plants a single zero in the two-byte pad between the root's entries, which the old rule
    ///     accepted and the tightened one rejects.
    /// </summary>
    [Fact]
    public void AZeroByteInAnAlignmentPadIsNotAcceptedAsFiller()
    {
        var image = BuildImage();

        // "resx" occupies bytes 0..17 of the root table and the next entry starts at 20, so bytes
        // 18 and 19 are the pad. On the real discs both are 0xFF.
        image[RootSector * SectorSize + 18] = 0;

        var path = WriteTemp(image);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<InvalidDataException>(() => XdvdfsVolume.Read(stream));
        Assert.Contains("does not consume the table exactly", error.Message, StringComparison.Ordinal);
        Assert.Contains("first is 0x00", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two directories whose entries name the same table sector. Nothing about either entry is
    ///     malformed and the walk could happily read the table once and move on — which is exactly
    ///     the silent under-report the reader must not produce, since the second directory's files
    ///     would simply be absent from an otherwise self-consistent census.
    /// </summary>
    [Fact]
    public void TwoDirectoriesSharingOneTableAreRefusedRatherThanCountedOnce()
    {
        var image = BuildImage();

        // Give the root a third child, "copy", pointing at resx's own table.
        WriteEntry(image, RootSector, 0, 0, 20 / 4, ResxSector, SectorSize, 0x10, "resx");
        WriteEntry(image, RootSector, 20, 0, 48 / 4, XbeSector, 100, 0x20, "default.xbe");
        WriteEntry(image, RootSector, 48, 0, 0, ResxSector, SectorSize, 0x10, "copy");

        var path = WriteTemp(image);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<InvalidDataException>(() => XdvdfsVolume.Read(stream));
        Assert.Contains("shares table sector", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A chain 33 directories deep, one past the reader's bound. The old code stopped enqueueing
    ///     at the bound with no error and no counter, so the deepest files just vanished; now the
    ///     volume is refused. (Retail bottoms out at <c>MaxTableDepth</c> 3 on the Brotherhood of
    ///     Steel disc and 4 on the Fallout: New Vegas X360 dump — both pinned in
    ///     <c>XdvdfsRetailTests</c> — so nothing real is anywhere near this. ⛔ An earlier version
    ///     of this comment said 3 for both, which was false.)
    /// </summary>
    [Fact]
    public void ATreeNestedPastTheDepthBoundIsRefusedRatherThanTruncated()
    {
        const int chain = 33;
        var image = new byte[(DescriptorSector + 2 + chain + 1) * SectorSize];
        var descriptor = DescriptorSector * SectorSize;
        Magic.CopyTo(image, descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(descriptor + 20), RootSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(descriptor + 24), 32);
        Magic.CopyTo(image, descriptor + 0x7EC);

        // root -> d0 -> d1 -> … -> d32, each table a 32-byte window holding one entry.
        for (var level = 0; level <= chain; level++)
        {
            var sector = RootSector + level;
            FillTable(image, sector);
            WriteEntry(image, sector, 0, 0, 0, (uint)(sector + 1), 32, 0x10, $"d{level}");
        }

        FillTable(image, RootSector + chain + 1);

        var path = WriteTemp(image);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<InvalidDataException>(() => XdvdfsVolume.Read(stream));
        Assert.Contains("nests deeper than the 32-level bound", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The folder census lists every directory the walk found, not only the ones that hold a
    ///     file. <c>resx/c1</c> here holds nothing but the <c>BAR</c> directory, and the interface
    ///     default — which derives folders from file paths — would drop it. On the retail discs that
    ///     default loses five directories each.
    /// </summary>
    [Fact]
    public void TheFolderCensusKeepsDirectoriesThatHoldNoFile()
    {
        var path = WriteTemp(BuildImage());
        using var backend = XdvdfsBackend.Open(path);
        var folders = backend.GetFolderStats();

        Assert.Equal(
            new[] { "(root)", "resx", "resx/c1", "resx/c1/BAR" },
            folders.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, folders["(root)"]);
        Assert.Equal(1, folders["resx"]);
        Assert.Equal(0, folders["resx/c1"]);
        Assert.Equal(1, folders["resx/c1/BAR"]);
    }

    [Fact]
    public void ADescriptorMissingItsTrailingMagicIsNotClaimed()
    {
        var image = BuildImage();
        image[DescriptorSector * SectorSize + 0x7EC] = 0;

        var path = WriteTemp(image);
        Assert.False(XdvdfsBackend.TryProbe(path));
    }

    [Fact]
    public void ARootExtentPastTheEndOfTheImageIsNotClaimed()
    {
        var image = BuildImage();
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(DescriptorSector * SectorSize + 20), TotalSectors + 8);

        var path = WriteTemp(image);
        Assert.False(XdvdfsBackend.TryProbe(path));
    }

    [Fact]
    public void APlainIso9660ImageIsLeftToTheDiscImageBackend()
    {
        // Sector 16 is a type-1 CD001 descriptor and there is no Xbox magic anywhere: the XDVDFS
        // probe must decline so the ISO9660 backend behind it in the chain still gets the file.
        var image = new byte[TotalSectors * SectorSize];
        image[16 * SectorSize + 0] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, 16 * SectorSize + 1);

        var path = WriteTemp(image);
        Assert.False(XdvdfsBackend.TryProbe(path));
    }

    [Fact]
    public void TheProbeIsGatedOnTheDiscImageExtensions()
    {
        // Same bytes, a non-image extension: the probe declines rather than paying for a seek on
        // every file the chain is handed. (.xiso is accepted alongside .iso.)
        var image = BuildImage();
        Assert.False(XdvdfsBackend.TryProbe(WriteTemp(image, ".bin")));
        Assert.True(XdvdfsBackend.TryProbe(WriteTemp(image, ".xiso")));
    }
}