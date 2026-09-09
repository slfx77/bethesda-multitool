using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Synthetic releases for the Arena v1.04 floppy installer reader. Two of the three block kinds
///     are built directly — a stored block (<c>clen == 0xFFFF</c>) and the empty terminator a
///     size-multiple-of-10,000 file ends on — and the LZHUF kind uses a compressed block whose
///     bytes are hand-derived from the codec's initial tree, not produced by the decoder under
///     test: symbol 0x00 sits at leaf slot 0, whose parent chain to the root spells the 9-bit code
///     1 1 0 0 0 1 1 0 0, which packs MSB-first into 0xC6 0x00 and decodes to a single 0x00 byte
///     (the same derivation <c>LzhufCodecTests</c> spells out in full).
///     <para>
///         The volumes are written one per "disk" directory, which is how a floppy-image dump lays
///         them out and therefore what the resolver has to cope with; a second test puts them all
///         in one directory instead.
///     </para>
/// </summary>
public class ArenaInstallerArchiveTests
{
    /// <summary>
    ///     Directory record length, measured on the retail ARENA.H1/H2/H8: 107 records tile the
    ///     three volumes' 10,272 bytes exactly at this stride. Written out as a literal so the
    ///     fixtures cannot drift with the production constant they exist to check.
    /// </summary>
    private const int RecordLength = 96;

    /// <summary>Uncompressed bytes a full block carries, measured on the retail stream.</summary>
    private const int FullBlockLength = 10000;

    /// <summary>Record type byte of the release's single directory record.</summary>
    private const byte DirectoryRecordType = 0x08;

    /// <summary>Record type byte of each of the 106 file records.</summary>
    private const byte FileRecordType = 0x02;

    /// <summary>Compressed-length word marking a block of <see cref="FullBlockLength" /> STORED bytes.</summary>
    private const int StoredMarker = 0xFFFF;

    /// <summary>An LZHUF block body decoding to exactly one 0x00 byte (see the class remarks).</summary>
    private static readonly byte[] SingleZeroByteLzhuf = [0xC6, 0x00];

    /// <summary>
    ///     Pins the production constants against the same retail-measured literals the fixtures are
    ///     built from. Without this the synthetic tests would stay green through a drift in any of
    ///     them, since every fixture would move with the code — the pattern this class shared with
    ///     <c>DaggerfallPackedArchiveTests</c> until 2026-09-07.
    /// </summary>
    [Fact]
    public void Constants_MatchTheShapeMeasuredOnTheRetailFloppies()
    {
        Assert.Equal(96, ArenaInstallerArchive.RecordLength);
        Assert.Equal(10000, ArenaInstallerArchive.FullBlockLength);
        Assert.Equal(0xFFFF, ArenaInstallerArchive.StoredMarker);
        Assert.Equal(0x08, ArenaInstallerArchive.DirectoryRecordType);
        Assert.Equal(0x02, ArenaInstallerArchive.FileRecordType);
    }

    [Fact]
    public void Parse_StoredAndCompressedAndEmptyBlocks_TileExactly()
    {
        var stored = new byte[FullBlockLength];
        new Random(5).NextBytes(stored);

        var release = new ReleaseBuilder("ARENA");
        release.AddStoredFile("BIG.DAT", stored);
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WritePerDisk(2);

        try
        {
            var anchor = Path.Combine(root, "Disk 1", "ARENA.H1");
            Assert.True(ArenaInstallerArchive.TryProbe(anchor));
            var archive = ArenaInstallerArchive.Parse(anchor);

            Assert.Equal(2, archive.Entries.Count);
            Assert.Equal(FullBlockLength + 1, archive.TotalSize);
            Assert.Equal("ARENA", archive.RootDirectory);

            // The stored file needs its empty terminator because its size is a multiple of 10,000.
            Assert.Equal(3, archive.BlockCount);
            Assert.Equal(1, archive.StoredBlockCount);
            Assert.Equal(1, archive.EmptyBlockCount);

            Assert.Equal(stored, archive.Extract(archive.Entries[0]));
            Assert.Equal(new byte[] { 0x00 }, archive.Extract(archive.Entries[1]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Parse_AllVolumesInOneDirectory_ResolvesToo()
    {
        var release = new ReleaseBuilder("ARENA");
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WriteFlat();

        try
        {
            var archive = ArenaInstallerArchive.Parse(FlatFile(root, "ARENA.1"));

            Assert.Equal("ONE.DAT", Assert.Single(archive.Entries).Name);
            Assert.Equal(1, archive.TotalSize);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryProbe_NameOtherThanAnAnchor_NotClaimed()
    {
        var release = new ReleaseBuilder("ARENA");
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WriteFlat();

        try
        {
            Assert.False(ArenaInstallerArchive.TryProbe(FlatFile(root, "ARENA.TDS")));
            Assert.False(ArenaInstallerArchive.IsAnchorName(FlatFile(root, "ARENA.H2")));
            Assert.True(ArenaInstallerArchive.IsAnchorName(FlatFile(root, "ARENA.H1")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Parse_TotalSizeDisagreeingWithTheDirectory_Rejected()
    {
        var release = new ReleaseBuilder("ARENA");
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WriteFlat();

        try
        {
            var tds = FlatFile(root, "ARENA.TDS");
            File.WriteAllBytes(tds, [0x02, 0x00, 0x00, 0x00]);

            var anchor = FlatFile(root, "ARENA.H1");
            Assert.False(ArenaInstallerArchive.TryProbe(anchor));
            var error = Assert.Throws<InvalidDataException>(() => ArenaInstallerArchive.Parse(anchor));
            Assert.Contains("ARENA.TDS declares 2", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Parse_TrailingByteAfterTheLastBlock_Rejected()
    {
        var release = new ReleaseBuilder("ARENA");
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WriteFlat();

        try
        {
            var data = FlatFile(root, "ARENA.1");
            File.WriteAllBytes(data, [.. File.ReadAllBytes(data), 0x00]);

            var anchor = FlatFile(root, "ARENA.H1");
            Assert.False(ArenaInstallerArchive.TryProbe(anchor));
            var error = Assert.Throws<InvalidDataException>(() => ArenaInstallerArchive.Parse(anchor));
            Assert.Contains("the walk consumed", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Parse_TdsMissing_Rejected()
    {
        var release = new ReleaseBuilder("ARENA");
        release.AddLzhufFile("ONE.DAT", SingleZeroByteLzhuf, 1);
        var root = release.WriteFlat();

        try
        {
            File.Delete(FlatFile(root, "ARENA.TDS"));

            var anchor = FlatFile(root, "ARENA.H1");
            Assert.False(ArenaInstallerArchive.TryProbe(anchor));
            var error = Assert.Throws<InvalidDataException>(() => ArenaInstallerArchive.Parse(anchor));
            Assert.Contains("ARENA.TDS is missing", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Path of <paramref name="name" /> inside a flat release written under <paramref name="root" />.</summary>
    private static string FlatFile(string root, string name)
    {
        return Path.Combine(root, ReleaseBuilder.VolumesFolder, name);
    }

    /// <summary>Assembles a release: one directory record, N file records, and the block stream.</summary>
    private sealed class ReleaseBuilder
    {
        /// <summary>Sub-directory a flat release is written into.</summary>
        public const string VolumesFolder = "Volumes";

        private readonly List<byte> _data = [];

        private readonly List<byte> _directory = [];
        private readonly string _rootName;
        private long _total;

        public ReleaseBuilder(string rootName)
        {
            _rootName = rootName;
            _directory.AddRange(Record(DirectoryRecordType, rootName, string.Empty, 0));
        }

        /// <summary>Adds a file carried by one stored block plus the empty terminator its size forces.</summary>
        public void AddStoredFile(string name, byte[] payload)
        {
            Assert.Equal(FullBlockLength, payload.Length);
            _directory.AddRange(Record(FileRecordType, name, _rootName, payload.Length));
            _total += payload.Length;

            _data.AddRange([StoredMarker & 0xFF, StoredMarker >> 8]);
            _data.AddRange(payload);

            // Empty terminator: clen 4 (counting the ulen word) with two flush bytes, ulen 0.
            _data.AddRange([0x04, 0x00, 0x00, 0x00, 0x00, 0x00]);
        }

        /// <summary>Adds a file carried by one LZHUF block whose body is supplied verbatim.</summary>
        public void AddLzhufFile(string name, byte[] body, int uncompressedLength)
        {
            _directory.AddRange(Record(FileRecordType, name, _rootName, uncompressedLength));
            _total += uncompressedLength;

            var header = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(body.Length + 2));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)uncompressedLength);
            _data.AddRange(header);
            _data.AddRange(body);
        }

        /// <summary>Writes the volumes one per "Disk n" directory, splitting on record/block edges.</summary>
        public string WritePerDisk(int volumes)
        {
            var root = NewRoot();
            var directory = _directory.ToArray();
            var data = _data.ToArray();

            for (var volume = 1; volume <= volumes; volume++)
            {
                var disk = Path.Combine(root, $"Disk {volume}");
                Directory.CreateDirectory(disk);

                // Everything on disk 1; the later disks carry empty volumes, exactly as the retail
                // release's ARENA.H3..H7 do.
                File.WriteAllBytes(
                    Path.Combine(disk, $"ARENA.H{volume}"), volume == 1 ? directory : []);
                File.WriteAllBytes(
                    Path.Combine(disk, $"ARENA.{volume}"), volume == 1 ? data : []);
            }

            File.WriteAllBytes(Path.Combine(root, "Disk 1", "ARENA.TDS"), TotalSizeBytes());
            return root;
        }

        /// <summary>
        ///     Writes a single-volume release with every file in one directory, nested one level
        ///     under the returned root so the resolver's sibling scan sees that one directory
        ///     rather than the whole temp folder.
        /// </summary>
        public string WriteFlat()
        {
            var root = NewRoot();
            var volumes = Path.Combine(root, VolumesFolder);
            Directory.CreateDirectory(volumes);
            File.WriteAllBytes(Path.Combine(volumes, "ARENA.H1"), _directory.ToArray());
            File.WriteAllBytes(Path.Combine(volumes, "ARENA.1"), _data.ToArray());
            File.WriteAllBytes(Path.Combine(volumes, "ARENA.TDS"), TotalSizeBytes());
            return root;
        }

        private byte[] TotalSizeBytes()
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)_total);
            return bytes;
        }

        private static string NewRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), $"arena_install_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return root;
        }

        private static byte[] Record(byte type, string name, string parent, long size)
        {
            var record = new byte[RecordLength];
            record[0] = type;
            Encoding.Latin1.GetBytes(name).CopyTo(record, 1);
            if (parent.Length > 0)
            {
                Encoding.Latin1.GetBytes(parent).CopyTo(record, 14);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(92), (uint)size);
            return record;
        }
    }
}