using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Travels.Dawnstar;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for Dawnstar's <c>.lmp</c> lump. The container has no magic beyond a
///     leading <c>-</c>, so its identity is pure arithmetic — the directory must end exactly where
///     the first payload starts and the payloads must tile to EOF. Every clause of that rule gets
///     its own rejection case here, because a loose probe would claim other files in the classic
///     chain.
/// </summary>
public sealed class DawnstarLumpArchiveTests
{
    private const string Name = "datfiles.lmp";

    /// <summary>Builds a well-formed lump from (name, payload) pairs.</summary>
    private static byte[] BuildLump(params (string Name, byte[] Payload)[] members)
    {
        var directoryLength = members.Sum(m => m.Name.Length + 2 + DawnstarLumpArchive.RecordLength);
        var directory = new List<byte>(directoryLength);
        var payloads = new List<byte>();
        var offset = directoryLength;

        foreach (var (name, payload) in members)
        {
            directory.Add(DawnstarLumpArchive.Delimiter);
            directory.AddRange(Encoding.ASCII.GetBytes(name));
            directory.Add(DawnstarLumpArchive.Delimiter);

            var record = new byte[DawnstarLumpArchive.RecordLength];
            BinaryPrimitives.WriteUInt32BigEndian(record, (uint)offset);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(4), (ushort)payload.Length);
            directory.AddRange(record);

            payloads.AddRange(payload);
            offset += payload.Length;
        }

        return [.. directory, .. payloads];
    }

    private static byte[] Payload(byte fill, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    private static byte[] SampleLump()
    {
        return BuildLump(
            ("charin.dat", Payload(0x11, 16)),
            ("geomin.dat", Payload(0x22, 222)),
            ("icons.png", Payload(0x33, 4)));
    }

    [Fact]
    public void Parse_WalksTheDirectoryAndSlicesEveryPayload()
    {
        var bytes = SampleLump();
        var archive = DawnstarLumpArchive.Parse(bytes, Name);

        Assert.Equal(Name, archive.Name);
        Assert.Equal(bytes.Length, archive.Length);
        Assert.Equal(3, archive.Entries.Length);

        // Directory length is exactly the sum of name + 2 dashes + 6-byte record, and the first
        // payload starts there — that is what ends the directory walk.
        var directoryLength = "charin.dat".Length + "geomin.dat".Length + "icons.png".Length + 3 * 8;
        Assert.Equal(directoryLength, archive.Entries[0].Offset);

        Assert.Equal(new[] { "charin.dat", "geomin.dat", "icons.png" }, archive.Entries.Select(e => e.Name));
        Assert.Equal(new[] { 0, 1, 2 }, archive.Entries.Select(e => e.DirectoryIndex));

        // Payloads tile: each starts where the previous ended, the last at EOF.
        for (var i = 1; i < archive.Entries.Length; i++)
        {
            Assert.Equal(archive.Entries[i - 1].Offset + archive.Entries[i - 1].Length, archive.Entries[i].Offset);
        }

        var last = archive.Entries[^1];
        Assert.Equal(bytes.Length, last.Offset + last.Length);

        var geom = archive.Entries[1];
        Assert.Equal(222, geom.Length);
        Assert.True(archive.Read(geom).Span.SequenceEqual(Payload(0x22, 222)));
    }

    [Fact]
    public void TryGetEntry_MatchesOrdinallyAndIsCaseSensitive()
    {
        var archive = DawnstarLumpArchive.Parse(SampleLump(), Name);

        Assert.True(archive.TryGetEntry("geomin.dat", out var entry));
        Assert.Equal(222, entry.Length);

        // The engine compares the raw directory token, so a case-folded name must MISS.
        Assert.False(archive.TryGetEntry("GEOMIN.DAT", out _));
        Assert.False(archive.TryGetEntry("missing.dat", out _));
    }

    [Fact]
    public void Read_ReturnsASliceOfTheLumpWithoutCopying()
    {
        var bytes = SampleLump();
        var archive = DawnstarLumpArchive.Parse(bytes, Name);

        Assert.True(archive.TryGetEntry("charin.dat", out var entry));
        var slice = archive.Read(entry);

        Assert.Equal(16, slice.Length);
        Assert.True(slice.Span.SequenceEqual(bytes.AsSpan(entry.Offset, entry.Length)));
    }

    [Fact]
    public void TryProbe_AcceptsAWellFormedLump()
    {
        Assert.True(DawnstarLumpArchive.TryProbe(SampleLump()));
    }

    public static TheoryData<string, byte[]> Rejections()
    {
        var data = new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "shorter than the smallest record", "-a-\0\0\0"u8.ToArray() },
            { "no leading dash", Encoding.ASCII.GetBytes("charin.dat is a plain file, not a lump.") },
            { "PNG signature", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D] }
        };

        // An empty name: "--" with a record behind it.
        var emptyName = BuildLump(("a", Payload(1, 4)));
        emptyName[1] = DawnstarLumpArchive.Delimiter;
        data.Add("empty name", emptyName);

        // A non-printable byte inside the name breaks the run before its closing dash.
        var badName = BuildLump(("ab", Payload(1, 4)));
        badName[2] = 0x07;
        data.Add("non-printable name byte", badName);

        // A record truncated by the end of the file.
        var truncated = BuildLump(("charin.dat", Payload(1, 4)));
        data.Add("truncated directory record", truncated[..14]);

        // A one-byte gap between two payloads: contiguity fails even though the tail still ends
        // at EOF.
        var gapped = BuildLump(("a.dat", Payload(1, 4)), ("b.dat", Payload(2, 4)));
        var secondRecord = 5 + 2 + DawnstarLumpArchive.RecordLength + 5 + 2;
        BinaryPrimitives.WriteUInt32BigEndian(
            gapped.AsSpan(secondRecord),
            BinaryPrimitives.ReadUInt32BigEndian(gapped.AsSpan(secondRecord)) + 1);
        data.Add("non-contiguous payloads", gapped);

        // The first offset lands inside the directory, so the walk overshoots it.
        var overshoot = BuildLump(("a.dat", Payload(1, 4)), ("b.dat", Payload(2, 4)));
        BinaryPrimitives.WriteUInt32BigEndian(overshoot.AsSpan(7), 8);
        data.Add("directory overruns the first payload", overshoot);

        // One byte too many at the end: the payloads no longer reach EOF exactly.
        var padded = BuildLump(("a.dat", Payload(1, 4)));
        data.Add("trailing byte after the last payload", [.. padded, 0x00]);

        // One byte short: the last payload runs past the file.
        var clipped = BuildLump(("a.dat", Payload(1, 4)));
        data.Add("last payload past EOF", clipped[..^1]);

        return data;
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void TryProbe_RejectsEverythingThatIsNotALump(string reason, byte[] bytes)
    {
        Assert.False(DawnstarLumpArchive.TryProbe(bytes), reason);
        Assert.ThrowsAny<InvalidDataException>(() => DawnstarLumpArchive.Parse(bytes, Name));
    }

    [Fact]
    public void Parse_NamesTheFileAndTheBytePositionOfTheViolation()
    {
        var gapped = BuildLump(("a.dat", Payload(1, 4)), ("b.dat", Payload(2, 4)));
        var secondRecord = 5 + 2 + DawnstarLumpArchive.RecordLength + 5 + 2;
        BinaryPrimitives.WriteUInt32BigEndian(
            gapped.AsSpan(secondRecord),
            BinaryPrimitives.ReadUInt32BigEndian(gapped.AsSpan(secondRecord)) + 1);

        var ex = Assert.Throws<InvalidDataException>(() => DawnstarLumpArchive.Parse(gapped, Name));

        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"at byte {secondRecord}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_OnAFile_AcceptsALumpAndRejectsItsNeighbours()
    {
        var directory = Directory.CreateTempSubdirectory("lmp-probe");
        try
        {
            var lump = Path.Combine(directory.FullName, "datfiles.lmp");
            var other = Path.Combine(directory.FullName, "charin.dat");
            File.WriteAllBytes(lump, SampleLump());
            File.WriteAllBytes(other, Payload(0x44, 1260));

            Assert.True(DawnstarLumpArchive.TryProbe(lump));
            Assert.False(DawnstarLumpArchive.TryProbe(other));
            Assert.False(DawnstarLumpArchive.TryProbe(Path.Combine(directory.FullName, "absent.lmp")));

            var archive = DawnstarLumpArchive.Parse(lump);
            Assert.Equal("datfiles.lmp", archive.Name);
            Assert.Equal(3, archive.Entries.Length);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Backend_SurfacesFlatEntriesWithTheirExtensions()
    {
        using var backend = new LmpArchiveBackend(DawnstarLumpArchive.Parse(SampleLump(), Name));

        Assert.Equal("LMP (Dawnstar)", backend.FormatName);
        Assert.Equal("J2ME", backend.PlatformLabel);
        Assert.Equal(3, backend.TotalFiles);

        var entries = backend.ListFiles();
        Assert.Equal(new[] { "charin.dat", "geomin.dat", "icons.png" }, entries.Select(e => e.FullPath));
        Assert.All(entries, entry =>
        {
            Assert.Equal(string.Empty, entry.FolderPath);
            Assert.False(entry.Compressed);
        });
        Assert.Equal(new[] { ".dat", ".dat", ".png" }, entries.Select(e => e.Extension));

        var geom = entries[1];
        Assert.Equal(222, geom.Size);
        Assert.Equal(Payload(0x22, 222), backend.Extract(geom));
    }
}