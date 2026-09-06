using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the cancelled PSP Oblivion's <c>GR.ARC</c> pack. The probe is pure
///     arithmetic — the <c>A2.0</c> tag cannot gate it, because the earliest retail beta has no tag
///     — so the rejection cases carry the weight here: each one breaks exactly one of the four
///     conditions that make the file tile.
/// </summary>
public sealed class OblivionPspArchiveTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"psp-arc-{Guid.NewGuid():N}.arc");
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

    /// <summary>
    ///     Builds a pack the way the game's own packer did: header, 16-byte records, 32-byte
    ///     aligned payloads in record order, then the NUL-terminated name table ending at EOF.
    ///     <paramref name="tagged" /> selects the later revision (an <c>A2.0</c> tag, a 32-aligned
    ///     data area and absolute payload offsets) or the June 2006 one (no tag, the raw table end,
    ///     relative offsets).
    /// </summary>
    private static byte[] Build(bool tagged, params (string Name, byte[] Data)[] entries)
    {
        var headerLength = tagged ? OblivionPspArchive.TaggedHeaderLength : OblivionPspArchive.UntaggedHeaderLength;
        var tableEnd = headerLength + (OblivionPspArchive.RecordLength * entries.Length);
        var dataStart = tagged ? (int)OblivionPspArchive.Align(tableEnd) : tableEnd;

        // Lay the payloads out in record space, aligning between them.
        var offsets = new int[entries.Length];
        var cursor = tagged ? dataStart : 0;
        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = cursor;
            cursor = (int)OblivionPspArchive.Align(cursor + entries[i].Data.Length);
        }

        var payloadEnd = (tagged ? cursor : dataStart + cursor);

        var names = new List<byte>();
        var nameOffsets = new int[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            nameOffsets[i] = names.Count;
            names.AddRange(Encoding.Latin1.GetBytes(entries[i].Name));
            names.Add(0);
        }

        var file = new byte[payloadEnd + names.Count];
        var span = file.AsSpan();

        var fields = span;
        if (tagged)
        {
            OblivionPspArchive.Magic.CopyTo(file, 0);
            fields = span[4..];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(fields, (uint)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[4..], (uint)dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[8..], (uint)payloadEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[12..], (uint)names.Count);

        for (var i = 0; i < entries.Length; i++)
        {
            var record = span.Slice(headerLength + (i * OblivionPspArchive.RecordLength), OblivionPspArchive.RecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)nameOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)entries[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], 0);

            var absolute = tagged ? offsets[i] : dataStart + offsets[i];
            entries[i].Data.CopyTo(file, absolute);
        }

        names.CopyTo(file, payloadEnd);
        return file;
    }

    private static byte[] Payload(string text) => Encoding.ASCII.GetBytes(text);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_ReadsBothRevisionsWithNamesAndPayloads(bool tagged)
    {
        var path = WriteTemp(Build(tagged,
            ("GlobalStream", Payload("the global stream payload")),
            ("Anticlere_1", Payload("a level stream")),
            ("unlockables.xml", Payload("<unlockables/>"))));

        var archive = OblivionPspArchive.Parse(path);

        Assert.Equal(tagged, archive.IsTagged);
        Assert.Equal(3, archive.Entries.Count);
        Assert.Equal(["GlobalStream", "Anticlere_1", "unlockables.xml"], archive.Entries.Select(e => e.Name));

        // The name table closes the file exactly, with no trailer.
        Assert.Equal(new FileInfo(path).Length, archive.NameTableOffset + archive.NameTableSize);

        using var reader = ArchiveReader.Open(path);
        Assert.Equal(3, reader.TotalFiles);
        Assert.Equal(Payload("a level stream"), reader.ReadFile("Anticlere_1"));
        Assert.Equal(Payload("<unlockables/>"), reader.ReadFile("unlockables.xml"));
    }

    [Fact]
    public void Parse_KeepsAZeroLengthEntry()
    {
        // The community-modified February 2007 disc truncates Hub_5_Demo to nothing but keeps its
        // record — a reader that treats size 0 as corruption loses the very thing that build shows.
        var path = WriteTemp(Build(true,
            ("Hub_5_Demo", []),
            ("GlobalStream", Payload("still here"))));

        var archive = OblivionPspArchive.Parse(path);
        Assert.Equal(0, archive.Entries[0].Size);

        using var reader = ArchiveReader.Open(path);
        Assert.Empty(reader.ReadFile("Hub_5_Demo")!);
    }

    [Fact]
    public void Parse_AcceptsAnUnorderedTable()
    {
        // The repacked disc appended its record instead of re-sorting, so uppercase-name ordering
        // is a property of Bethesda's packer and must never be a parse requirement.
        var path = WriteTemp(Build(true,
            ("Weapons", Payload("w")),
            ("Credits", Payload("Hack by NexTheReal"))));

        var archive = OblivionPspArchive.Parse(path);
        Assert.Equal(["Weapons", "Credits"], archive.Entries.Select(e => e.Name));
        Assert.Equal("Hack by NexTheReal", Encoding.ASCII.GetString(
            ArchiveReader.Open(path).ReadFile("Credits")!));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var archive = OblivionPspArchive.Parse(WriteTemp(Build(true, ("GlobalStream", Payload("x")))));
        Assert.NotNull(archive.Find("globalstream"));
        Assert.Null(archive.Find("absent"));
    }

    [Fact]
    public void TryProbe_RejectsANameTableThatDoesNotEndAtEof()
    {
        var bytes = Build(true, ("A", Payload("a")));
        // Shrink the declared name table by one byte: it no longer closes the file.
        var sizeField = 4 + 12;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(sizeField), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sizeField)) - 1);

        var path = WriteTemp(bytes);
        Assert.False(OblivionPspArchive.TryProbe(path));
        var ex = Assert.Throws<InvalidDataException>(() => OblivionPspArchive.Parse(path));
        Assert.Contains("name table", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_RejectsAMisalignedDataStart()
    {
        var bytes = Build(true, ("A", Payload("a")));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + 4), 999);

        var path = WriteTemp(bytes);
        Assert.False(OblivionPspArchive.TryProbe(path));
        Assert.Contains("dataStart", Assert.Throws<InvalidDataException>(() => OblivionPspArchive.Parse(path)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryProbe_RejectsAPayloadRunWithAHole()
    {
        // Push the second payload 32 bytes further on: the run no longer tiles, even though every
        // extent still lies inside the file.
        var bytes = Build(true, ("A", Payload("aaaa")), ("B", Payload("bbbb")));
        var secondRecord = OblivionPspArchive.TaggedHeaderLength + OblivionPspArchive.RecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(secondRecord + 4),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(secondRecord + 4)) + 32);

        Assert.False(OblivionPspArchive.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void TryProbe_RejectsANonZeroReservedDword()
    {
        var bytes = Build(true, ("A", Payload("a")));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(OblivionPspArchive.TaggedHeaderLength + 12), 1);

        Assert.False(OblivionPspArchive.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void TryProbe_RejectsAnUnterminatedName()
    {
        var bytes = Build(true, ("A", Payload("a")));
        // Overwrite the single NUL that terminates the only name.
        bytes[^1] = (byte)'!';

        Assert.False(OblivionPspArchive.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void TryProbe_RejectsUnrelatedFiles()
    {
        // The first of these opens with the PKZIP signature and must STILL be refused: this
        // probe is pure arithmetic, and the zip backend sits behind it in the chain to claim
        // real archives.
        Assert.False(OblivionPspArchive.TryProbe(WriteTemp(Encoding.ASCII.GetBytes("PK\u0003\u0004 not an arc"))));
        Assert.False(OblivionPspArchive.TryProbe(WriteTemp([])));
        Assert.False(OblivionPspArchive.TryProbe(WriteTemp(new byte[64])));
    }

    [Fact]
    public void Align_RoundsUpToThe32ByteBoundary()
    {
        Assert.Equal(0, OblivionPspArchive.Align(0));
        Assert.Equal(32, OblivionPspArchive.Align(1));
        Assert.Equal(32, OblivionPspArchive.Align(32));
        Assert.Equal(64, OblivionPspArchive.Align(33));

        // The June revision's table end is exactly the value that is NOT aligned, which is why that
        // build had to store relative offsets.
        Assert.Equal(1056, OblivionPspArchive.Align(1040));
    }
}
