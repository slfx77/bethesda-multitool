using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The cut-1c resolver's container defenses on synthetic archives, without any real asset: the name-at-index
///     rejection in both container walks (a reshuffled archive is rejected rather than silently resliced), the ROB
///     segment-type rejection, and the stored-pin rules for LZSS entries. Each rejection test has a matching positive
///     read of the same fixture, so a rejection proves the named check fired and not a broken fixture; deleting any
///     of the resolver's checks makes the corresponding rejection test fail because bytes come back instead.
/// </summary>
/// <remarks>
///     Fixtures are written to unique temp files because the production parsers
///     (<c>XnGineBsaParser</c>, <c>RedguardRobParser</c>) take paths and the resolver caches each parsed directory
///     per absolute path. The synthetic named-form XnGine BSA is two uncompressed entries (plus a one-entry
///     LZSS-flagged variant); the synthetic ROB is two segments of types 0 and 512.
/// </remarks>
public sealed class Cut1cFixtureResolverTests : IDisposable
{
    private const string AnySha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly byte[] PayloadA = Encoding.ASCII.GetBytes("MESH-A-PAYLOAD--");
    private static readonly byte[] PayloadB = Encoding.ASCII.GetBytes("MESH-B-BYTES");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cut1c-resolver-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    internal void BsaEntry_RejectsAWrongNameAtTheIndex()
    {
        var location = Write(NamedBsa(("A.3D", PayloadA, false), ("B.3D", PayloadB, false)));
        var candidate = BsaCandidate("B.3D", 0);
        var (bytes, label) = Cut1cFixtureResolver.ReadBsaEntry(location, candidate, "control");
        Assert.Null(bytes);
        Assert.Contains("index 0 names 'A.3D', not 'B.3D'", label, StringComparison.Ordinal);
    }

    [Fact]
    internal void BsaEntry_ReadsTheEntryWhoseNameMatchesItsIndex()
    {
        var location = Write(NamedBsa(("A.3D", PayloadA, false), ("B.3D", PayloadB, false)));
        var (bytes, _) = Cut1cFixtureResolver.ReadBsaEntry(location, BsaCandidate("B.3D", 1), "control");
        Assert.NotNull(bytes);
        Assert.Equal(PayloadB, bytes);
    }

    [Fact]
    internal void BsaEntry_RejectsACompressedEntryWithoutStoredPins()
    {
        var location = Write(NamedBsa(("C.3D", PayloadA, true)));
        var (bytes, label) = Cut1cFixtureResolver.ReadBsaEntry(location, BsaCandidate("C.3D", 0), "control");
        Assert.Null(bytes);
        Assert.Contains("the entry is LZSS-compressed but the manifest pins no stored digest", label,
            StringComparison.Ordinal);
    }

    [Fact]
    internal void BsaEntry_RejectsACompressedEntryWhoseStoredBytesMismatch()
    {
        var location = Write(NamedBsa(("C.3D", PayloadA, true)));
        var candidate = BsaCandidate("C.3D", 0) with { StoredSize = PayloadA.Length, StoredSha256 = AnySha };
        var (bytes, label) = Cut1cFixtureResolver.ReadBsaEntry(location, candidate, "control");
        Assert.Null(bytes);
        Assert.Contains("stored SHA-256 mismatch", label, StringComparison.Ordinal);
    }

    [Fact]
    internal void BsaEntry_RejectsStoredPinsOnAnUncompressedEntry()
    {
        var location = Write(NamedBsa(("A.3D", PayloadA, false)));
        var candidate = BsaCandidate("A.3D", 0) with { StoredSize = PayloadA.Length, StoredSha256 = AnySha };
        var (bytes, label) = Cut1cFixtureResolver.ReadBsaEntry(location, candidate, "control");
        Assert.Null(bytes);
        Assert.Contains("the manifest pins stored bytes but the entry is not compressed", label,
            StringComparison.Ordinal);
    }

    [Fact]
    internal void RobSegment_RejectsAWrongNameAtTheIndex()
    {
        var location = Write(Rob(("SEG0", 0u, PayloadA), ("SEG1", 512u, PayloadB)));
        var candidate = RobCandidate("SEG1", 0, segmentType: 0);
        var (bytes, label) = Cut1cFixtureResolver.ReadRobSegment(location, candidate, "control");
        Assert.Null(bytes);
        Assert.Contains("segment 0 names 'SEG0', not 'SEG1'", label, StringComparison.Ordinal);
    }

    [Fact]
    internal void RobSegment_RejectsAWrongSegmentType()
    {
        var location = Write(Rob(("SEG0", 0u, PayloadA), ("SEG1", 512u, PayloadB)));
        var candidate = RobCandidate("SEG0", 0, segmentType: 512);
        var (bytes, label) = Cut1cFixtureResolver.ReadRobSegment(location, candidate, "control");
        Assert.Null(bytes);
        Assert.Contains("segment 0 has type 0, not 512", label, StringComparison.Ordinal);
    }

    [Fact]
    internal void RobSegment_ReadsTheSegmentWhoseNameAndTypeMatch()
    {
        var location = Write(Rob(("SEG0", 0u, PayloadA), ("SEG1", 512u, PayloadB)));
        var (bytes, _) = Cut1cFixtureResolver.ReadRobSegment(location, RobCandidate("SEG1", 1, 512), "control");
        Assert.NotNull(bytes);
        Assert.Equal(PayloadB, bytes);
    }

    private static Cut1cCoverSource BsaCandidate(string entry, int index)
    {
        return new Cut1cCoverSource("Sample/synthetic/3D.BSA", entry, index,
            Cut1cCoverFile.XnGineBsaContainer, StoredSize: null, StoredSha256: null, SegmentType: null);
    }

    private static Cut1cCoverSource RobCandidate(string entry, int index, int segmentType)
    {
        return new Cut1cCoverSource("Sample/synthetic/D.ROB", entry, index,
            Cut1cCoverFile.RobContainer, StoredSize: null, StoredSha256: null, segmentType);
    }

    /// <summary>A named-form XnGine BSA: u16 count + u16 0x0100, payloads from 4, 18-byte records at EOF.</summary>
    private static byte[] NamedBsa(params (string Name, byte[] Payload, bool Compressed)[] entries)
    {
        using var stream = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(word[2..], 0x0100);
        stream.Write(word);
        foreach (var (_, payload, _) in entries)
        {
            stream.Write(payload);
        }

        Span<byte> record = stackalloc byte[18];
        foreach (var (name, payload, compressed) in entries)
        {
            record.Clear();
            Encoding.ASCII.GetBytes(name, record[..12]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], compressed ? (ushort)0x0100 : (ushort)0);
            BinaryPrimitives.WriteInt32LittleEndian(record[14..], payload.Length);
            stream.Write(record);
        }

        return stream.ToArray();
    }

    /// <summary>A ROB: OARC(4) = count, OARD(length), 80-byte segment headers + payloads, "END ".</summary>
    private static byte[] Rob(params (string Name, uint Type, byte[] Payload)[] segments)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[20];
        "OARC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)segments.Length);
        "OARD"u8.CopyTo(header[12..]);
        var dataLength = segments.Sum(s => 80 + s.Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)dataLength);
        stream.Write(header);

        Span<byte> segment = stackalloc byte[80];
        foreach (var (name, type, payload) in segments)
        {
            segment.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(segment, (uint)(80 + payload.Length));
            Encoding.ASCII.GetBytes(name, segment.Slice(4, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(segment[12..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(segment[76..], (uint)payload.Length);
            stream.Write(segment);
            stream.Write(payload);
        }

        stream.Write("END "u8);
        return stream.ToArray();
    }

    private string Write(byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
