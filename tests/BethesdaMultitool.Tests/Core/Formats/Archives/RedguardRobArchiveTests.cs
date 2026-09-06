using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for <see cref="RedguardRobParser" /> and its backend, shaped after the 41
///     retail archives surveyed 2026-09-04. The probe leans on exact arithmetic — chunk length,
///     forward pointers and the <c>"END "</c> terminator must all agree — so the rejection cases
///     carry as much weight as the accept ones.
/// </summary>
public sealed class RedguardRobArchiveTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redguard-rob-{Guid.NewGuid():N}.rob");
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

    private static byte[] Build(params (string Name, uint Type, byte[] Data)[] segments)
    {
        var body = new List<byte>();
        foreach (var segment in segments)
        {
            var header = new byte[RedguardRobParser.SegmentHeaderLength];

            // Forward pointer, then the 8-byte NUL-padded name, then the type word.
            BinaryPrimitives.WriteUInt32LittleEndian(
                header, (uint)(RedguardRobParser.SegmentHeaderLength + segment.Data.Length));
            Encoding.ASCII.GetBytes(segment.Name).CopyTo(header, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), segment.Type);

            // Size is the last of the header's twenty dwords.
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), (uint)segment.Data.Length);

            body.AddRange(header);
            body.AddRange(segment.Data);
        }

        var bytes = new List<byte>(RedguardRobParser.HeaderLength + body.Count + 4);
        bytes.AddRange("OARC"u8.ToArray());
        bytes.AddRange([0, 0, 0, 4]); // big-endian chunk length of 4
        bytes.AddRange(BitConverter.GetBytes((uint)segments.Length)); // little-endian payload
        bytes.AddRange("OARD"u8.ToArray());

        var dataLength = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(dataLength, (uint)body.Count);
        bytes.AddRange(dataLength);

        bytes.AddRange(body);
        bytes.AddRange("END "u8.ToArray());
        return [.. bytes];
    }

    /// <summary>A stand-in payload with the version tag every retail segment starts with.</summary>
    private static byte[] Mesh(string version = "v2.7") => [.. Encoding.ASCII.GetBytes(version), 1, 2, 3, 4];

    [Fact]
    public void Parse_ReadsSegmentsInFileOrderWithPayloadsAfterEachHeader()
    {
        var path = WriteTemp(Build(
            ("GR_COMP", 0u, Mesh()),
            ("CYSWD1", 512u, Mesh("v2.6"))));

        var archive = RedguardRobParser.Parse(path);

        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(("GR_COMP", 100L, 8, 0u), (
            archive.Entries[0].Name, archive.Entries[0].Offset, archive.Entries[0].Size, archive.Entries[0].Type));

        // Second header starts right after the first payload: 20 + 80 + 8 = 108, body at 188.
        Assert.Equal(("CYSWD1", 188L, 8, 512u), (
            archive.Entries[1].Name, archive.Entries[1].Offset, archive.Entries[1].Size, archive.Entries[1].Type));
    }

    [Fact]
    public void Parse_EmptySegment_IsKeptAsANamedEntry()
    {
        // 1,203 of the 5,870 retail segments are empty placeholders that still carry a name; the
        // reference exporter skips writing them but they are real directory entries.
        var path = WriteTemp(Build(("HOLLOW", 0u, []), ("XMONEY", 0u, Mesh())));

        var archive = RedguardRobParser.Parse(path);

        Assert.Equal(["HOLLOW", "XMONEY"], archive.Entries.Select(e => e.Name));
        Assert.Equal(0, archive.Entries[0].Size);
    }

    [Fact]
    public void Extract_SegmentPayload_IsByteExactAndUncompressed()
    {
        // Nothing in a ROB is compressed — GR_COMP is a mesh name, not a compression marker.
        var payload = Mesh();
        var path = WriteTemp(Build(("GR_COMP", 0u, payload)));

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("ROB (Redguard)", reader.FormatName);
        Assert.Equal(payload, reader.ReadFile("GR_COMP.3D"));
        Assert.False(Assert.Single(reader.ListFiles()).Compressed);
    }

    [Fact]
    public void ListFiles_SurfacesSegmentsAsThreeDeeMeshes()
    {
        var path = WriteTemp(Build(("HIRONSK", 0u, Mesh()), ("HOLLOW", 0u, [])));

        using var reader = ArchiveReader.Open(path);
        var entries = reader.ListFiles();

        Assert.Equal(2, reader.TotalFiles);
        Assert.Equal(["HIRONSK.3D", "HOLLOW.3D"], entries.Select(e => e.FullPath));
        Assert.All(entries, entry => Assert.Equal(".3d", entry.Extension));
        Assert.All(entries, entry => Assert.Empty(entry.FolderPath));
    }

    [Fact]
    public void Probe_MissingTerminator_IsRejected()
    {
        var bytes = Build(("GR_COMP", 0u, Mesh()));
        bytes[^1] = (byte)'X';

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_ChunkLengthThatDoesNotCoverTheSegments_IsRejected()
    {
        var bytes = Build(("GR_COMP", 0u, Mesh()));

        // Shrink the OARD chunk's big-endian length by one byte.
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(16), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) - 1);

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_ForwardPointerDisagreeingWithSize_IsRejected()
    {
        // Both routes through the file agree on all 41 retail archives, so a mismatch means the
        // walk has desynchronised rather than that a variant exists.
        var bytes = Build(("GR_COMP", 0u, Mesh()));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardRobParser.HeaderLength), 999u);

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_WrongCountChunkLength_IsRejected()
    {
        var bytes = Build(("GR_COMP", 0u, Mesh()));
        bytes[7] = 8; // the OARC chunk holds exactly one dword

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_SegmentCountBeyondTheChunk_IsRejected()
    {
        var bytes = Build(("GR_COMP", 0u, Mesh()));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4u);

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Parse_EightCharacterName_HasNoTerminatorAndIsAccepted()
    {
        // The common case, not an edge one: 3,850 of the 5,870 retail segments fill the field
        // exactly. What terminates them is the type word behind it, whose low byte is always zero.
        var path = WriteTemp(Build(("GSWORD1A", 0u, Mesh())));

        Assert.Equal("GSWORD1A", Assert.Single(RedguardRobParser.Parse(path).Entries).Name);
    }

    [Fact]
    public void Probe_NonPrintableName_IsRejected()
    {
        var bytes = Build(("GR_COMP", 0u, Mesh()));
        bytes[RedguardRobParser.HeaderLength + 4] = 0x01;

        Assert.False(RedguardRobParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_RejectsOtherArchiveFamilies()
    {
        foreach (var head in new[] { "BSA\09999", "BTDX9999" })
        {
            Assert.False(RedguardRobParser.TryProbe(WriteTemp(Encoding.ASCII.GetBytes(head + new string('x', 96)))));
        }

        // An XnGine BSA opens with a u16 count and a u16 type word — no chunk magic at all.
        var xngine = new byte[96];
        xngine[0] = 1;
        xngine[3] = 0x01;
        Assert.False(RedguardRobParser.TryProbe(WriteTemp(xngine)));
    }

    [Fact]
    public void Parse_NonRob_ThrowsWithTheFileNamed()
    {
        var path = WriteTemp(Encoding.ASCII.GetBytes(new string('x', 96)));

        var error = Assert.Throws<InvalidDataException>(() => RedguardRobParser.Parse(path));

        Assert.Contains(Path.GetFileName(path), error.Message, StringComparison.Ordinal);
    }
}
