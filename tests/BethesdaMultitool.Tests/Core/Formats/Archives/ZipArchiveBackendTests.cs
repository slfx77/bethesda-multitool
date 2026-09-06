using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Zip;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for <see cref="PkZipParser" /> and the PKZIP backend behind
///     <see cref="ArchiveReader" />. The probe is exact — end record at EOF, central directory
///     tiling exactly up to it, the declared header count walking exactly to its end — so the
///     rejection cases matter as much as the accept ones. Archives are written by the BCL
///     <see cref="ZipArchive" /> writer (an independent implementation of the same APPNOTE), then
///     corrupted byte-wise where a rejection is being pinned.
/// </summary>
public sealed class ZipArchiveBackendTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes, string extension = ".jar")
    {
        var path = Path.Combine(Path.GetTempPath(), $"pkzip-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

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

    private static byte[] Build(params (string Name, byte[] Data, CompressionLevel Level)[] members)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var member in members)
            {
                var entry = zip.CreateEntry(member.Name, member.Level);
                using var writer = entry.Open();
                writer.Write(member.Data);
            }
        }

        return stream.ToArray();
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    /// <summary>A payload long enough that DEFLATE actually shrinks it, so the two methods are distinguishable.</summary>
    private static byte[] Repetitive(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 7 == 0 ? 'A' : 'B')).ToArray();

    [Fact]
    public void Parse_ReadsEveryMemberWithItsMethodAndSizes()
    {
        var big = Repetitive(4000);
        var path = WriteTemp(Build(
            ("META-INF/MANIFEST.MF", Text("Manifest-Version: 1.0\n"), CompressionLevel.Optimal),
            ("charin.dat", Text("stored-table"), CompressionLevel.NoCompression),
            ("art/bag.cus", big, CompressionLevel.Optimal)));

        var archive = PkZipParser.Parse(path);

        Assert.True(archive.IsJavaArchive);
        Assert.Equal(3, archive.Entries.Count);

        var stored = archive.Entries.Single(e => e.Name == "charin.dat");
        Assert.True(stored.IsStored);
        Assert.Equal(12u, stored.CompressedSize);
        Assert.Equal(12u, stored.UncompressedSize);

        var deflated = archive.Entries.Single(e => e.Name == "art/bag.cus");
        Assert.True(deflated.IsDeflated);
        Assert.Equal(4000u, deflated.UncompressedSize);
        Assert.True(deflated.CompressedSize < deflated.UncompressedSize);

        // The end record is the last 22 bytes and the directory ends exactly there.
        Assert.Equal(new FileInfo(path).Length - PkZipParser.EndRecordLength, archive.EndOfCentralDirectoryOffset);
        Assert.True(archive.CentralDirectoryOffset < archive.EndOfCentralDirectoryOffset);
    }

    [Fact]
    public void Extract_RoundTripsStoredAndDeflatedPayloads()
    {
        var big = Repetitive(4000);
        var path = WriteTemp(Build(
            ("charin.dat", Text("stored-table"), CompressionLevel.NoCompression),
            ("art/bag.cus", big, CompressionLevel.Optimal)));
        var archive = PkZipParser.Parse(path);

        Assert.Equal(Text("stored-table"), PkZipParser.Extract(archive, archive.Entries[0]));
        Assert.Equal(big, PkZipParser.Extract(archive, archive.Entries[1]));
    }

    [Fact]
    public void ArchiveReader_OpensAZipThroughTheProbeAndListsFilesWithFolders()
    {
        var path = WriteTemp(Build(
            ("META-INF/MANIFEST.MF", Text("Manifest-Version: 1.0\n"), CompressionLevel.Optimal),
            ("charin.dat", Text("x"), CompressionLevel.NoCompression),
            ("ngame/midlet/a.class", Text("class"), CompressionLevel.Optimal)));

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("JAR (PKZIP)", reader.FormatName);
        Assert.Equal("J2ME", reader.PlatformLabel);
        Assert.Equal(3, reader.TotalFiles);

        var entries = reader.ListFiles();
        var nested = entries.Single(e => e.Name == "a.class");
        Assert.Equal(@"ngame\midlet\a.class", nested.FullPath);
        Assert.Equal(@"ngame\midlet", nested.FolderPath);
        Assert.Equal(".class", nested.Extension);
        Assert.True(nested.Compressed);
        Assert.Equal(5, nested.Size);

        var stored = entries.Single(e => e.Name == "charin.dat");
        Assert.False(stored.Compressed);
        Assert.Equal(string.Empty, stored.FolderPath);
        Assert.Equal(Text("x"), reader.Extract(stored));
        Assert.Equal(Text("class"), reader.ReadFile("ngame/midlet/a.class"));
    }

    [Fact]
    public void ArchiveReader_NonJarZipReportsPlainZip()
    {
        var path = WriteTemp(Build(("core/entities/a.ent", Text("ent"), CompressionLevel.Optimal)), ".bos");

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("ZIP (PKZIP)", reader.FormatName);
        Assert.Equal("PC", reader.PlatformLabel);
    }

    [Fact]
    public void ListFiles_OmitsDirectoryPlaceholderEntries()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("META-INF/");
            var entry = zip.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.NoCompression);
            using var writer = entry.Open();
            writer.Write(Text("m"));
        }

        var path = WriteTemp(stream.ToArray());
        var archive = PkZipParser.Parse(path);
        Assert.Equal(2, archive.Entries.Count);
        Assert.True(archive.Entries[0].IsDirectory);

        using var reader = ArchiveReader.Open(path);
        Assert.Equal(1, reader.TotalFiles);
        Assert.Single(reader.ListFiles());
    }

    [Fact]
    public void Extract_DetectsACorruptedStoredPayloadByCrc()
    {
        var bytes = Build(("charin.dat", Text("stored-table"), CompressionLevel.NoCompression));

        // Flip a payload byte: the local header is 30 bytes + the 10-byte name, so the data starts at 40.
        var index = Array.IndexOf(bytes, (byte)'s', 40);
        Assert.True(index >= 40);
        bytes[index] ^= 0x01;

        var path = WriteTemp(bytes);
        var archive = PkZipParser.Parse(path);
        var ex = Assert.Throws<InvalidDataException>(() => PkZipParser.Extract(archive, archive.Entries[0]));
        Assert.Contains("CRC-32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RefusesAnEncryptedEntry()
    {
        var bytes = Build(("charin.dat", Text("stored-table"), CompressionLevel.NoCompression));
        var archive = PkZipParser.Parse(WriteTemp(bytes));

        // Set the encryption bit in the central header's flags (offset 8 within the header).
        var flagsOffset = (int)archive.CentralDirectoryOffset + 8;
        bytes[flagsOffset] |= 0x01;
        var path = WriteTemp(bytes);

        var patched = PkZipParser.Parse(path);
        Assert.True(patched.Entries[0].IsEncrypted);
        Assert.Throws<InvalidDataException>(() => PkZipParser.Extract(patched, patched.Entries[0]));
    }

    [Fact]
    public void Extract_RefusesAnUnsupportedMethod()
    {
        var bytes = Build(("charin.dat", Text("stored-table"), CompressionLevel.NoCompression));
        var archive = PkZipParser.Parse(WriteTemp(bytes));

        // Method 12 (bzip2) in the central header (offset 10).
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((int)archive.CentralDirectoryOffset + 10), 12);
        var path = WriteTemp(bytes);

        var patched = PkZipParser.Parse(path);
        var ex = Assert.Throws<InvalidDataException>(() => PkZipParser.Extract(patched, patched.Entries[0]));
        Assert.Contains("method 12", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_RejectsAFileThatDoesNotStartWithALocalHeader()
    {
        var bytes = Build(("a.dat", Text("x"), CompressionLevel.NoCompression));
        bytes[0] = (byte)'Q';
        Assert.False(PkZipParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void TryProbe_RejectsATruncatedEndRecord()
    {
        var bytes = Build(("a.dat", Text("x"), CompressionLevel.NoCompression));
        Assert.False(PkZipParser.TryProbe(WriteTemp(bytes[..^1])));
    }

    [Fact]
    public void TryProbe_RejectsTrailingBytesAfterTheEndRecord()
    {
        // The end record must END at EOF; a byte appended after it (with a zero comment length)
        // breaks the exact-tail condition even though the signature is still findable.
        var bytes = Build(("a.dat", Text("x"), CompressionLevel.NoCompression));
        Assert.False(PkZipParser.TryProbe(WriteTemp([.. bytes, 0x00])));
    }

    [Fact]
    public void TryProbe_RejectsACentralDirectoryOffsetThatDoesNotLandOnTheEndRecord()
    {
        var bytes = Build(("a.dat", Text("x"), CompressionLevel.NoCompression));
        var endOffset = bytes.Length - PkZipParser.EndRecordLength;
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(endOffset + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(endOffset + 16), directoryOffset - 1);

        Assert.False(PkZipParser.TryProbe(WriteTemp(bytes)));
        var ex = Assert.Throws<InvalidDataException>(() => PkZipParser.Parse(WriteTemp(bytes)));
        Assert.Contains("central directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_RejectsAnEntryCountThatDoesNotWalkTheDirectoryExactly()
    {
        var bytes = Build(
            ("a.dat", Text("x"), CompressionLevel.NoCompression),
            ("b.dat", Text("y"), CompressionLevel.NoCompression));
        var endOffset = bytes.Length - PkZipParser.EndRecordLength;

        // Declare one entry where two headers exist: the walk stops short of the directory end.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(endOffset + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(endOffset + 10), 1);

        Assert.False(PkZipParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void TryProbe_RejectsZip64Sentinels()
    {
        var bytes = Build(("a.dat", Text("x"), CompressionLevel.NoCompression));
        var endOffset = bytes.Length - PkZipParser.EndRecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(endOffset + 12), uint.MaxValue);

        Assert.False(PkZipParser.TryProbe(WriteTemp(bytes)));
        var ex = Assert.Throws<InvalidDataException>(() => PkZipParser.Parse(WriteTemp(bytes)));
        Assert.Contains("ZIP64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_RejectsAnArchiveThatIsNotAZipAtAll()
    {
        Assert.False(PkZipParser.TryProbe(WriteTemp(Encoding.ASCII.GetBytes("BSA\0not really"), ".bsa")));
        Assert.False(PkZipParser.TryProbe(WriteTemp([], ".jar")));
    }

    [Fact]
    public void ComputeCrc32_MatchesTheKnownCheckValue()
    {
        // The CRC-32 check value for "123456789" is 0xCBF43926 (IEEE 802.3 / PKZIP).
        Assert.Equal(0xCBF43926u, PkZipParser.ComputeCrc32(Text("123456789")));
        Assert.Equal(0u, PkZipParser.ComputeCrc32(ReadOnlySpan<byte>.Empty));
    }
}
